﻿// --------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.
// --------------------------------------------------------------------------------------------

using System;
using System.IO;
using System.Linq;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Oryx.BuildScriptGenerator;
using Microsoft.Oryx.BuildScriptGeneratorCli.Options;
using Microsoft.Oryx.Common;

namespace Microsoft.Oryx.BuildScriptGeneratorCli
{
    [Command(Name, Description = "Execute an arbitrary command in the default shell, in an environment " +
        "with the best-matching platform tool versions.")]
    internal class ExecCommand : CommandBase
    {
        public const string Name = "exec";

        [Option("-s|--src <dir>", CommandOptionType.SingleValue, Description = "Source directory.")]
        [DirectoryExists]
        public string SourceDir { get; set; }

        [Argument(1, Description = "The command to execute in an app-specific environment.")]
        public string Command { get; set; }

        internal override int Execute(IServiceProvider serviceProvider, IConsole console)
        {
            var logger = serviceProvider.GetRequiredService<ILogger<ExecCommand>>();
            var env = serviceProvider.GetRequiredService<IEnvironment>();
            var opts = serviceProvider.GetRequiredService<IOptions<BuildScriptGeneratorOptions>>().Value;

            var beginningOutputLog = GetBeginningCommandOutputLog();
            console.WriteLine(beginningOutputLog);

            if (string.IsNullOrWhiteSpace(Command))
            {
                logger.LogDebug("Command is empty; exiting");
                return ProcessConstants.ExitSuccess;
            }

            var shellPath = env.GetEnvironmentVariable("BASH") ?? FilePaths.Bash;
            var context = BuildScriptGenerator.CreateContext(serviceProvider, operationId: null);
            var detector = serviceProvider.GetRequiredService<DefaultPlatformDetector>();
            var detectedPlatforms = detector.DetectPlatforms(context);
            if (!detectedPlatforms.Any())
            {
                return ProcessConstants.ExitFailure;
            }

            int exitCode;
            using (var timedEvent = logger.LogTimedEvent("ExecCommand"))
            {
                // Build envelope script
                var scriptBuilder = new ShellScriptBuilder("\n")
                    .AddShebang(shellPath)
                    .AddCommand("set -e");

                var envSetupProvider = serviceProvider.GetRequiredService<PlatformsInstallationScriptProvider>();
                var installationScript = envSetupProvider.GetBashScriptSnippet(
                    context,
                    detectedPlatforms);
                if (!string.IsNullOrEmpty(installationScript))
                {
                    scriptBuilder.AddCommand(installationScript);
                }

                scriptBuilder.Source(
                    $"{FilePaths.Benv} " +
                    $"{string.Join(" ", detectedPlatforms.Select(p => $"{p.Platform}={p.PlatformVersion}"))}");

                scriptBuilder
                    .AddCommand("echo Executing supplied command...")
                    .AddCommand(Command);

                // Create temporary file to store script
                // Get the path where the generated script should be written into.
                var tempDirectoryProvider = serviceProvider.GetRequiredService<ITempDirectoryProvider>();
                var tempScriptPath = Path.Combine(tempDirectoryProvider.GetTempDirectory(), "execCommand.sh");
                var script = scriptBuilder.ToString();
                File.WriteAllText(tempScriptPath, script);
                console.WriteLine("Finished generating script.");

                timedEvent.AddProperty(nameof(tempScriptPath), tempScriptPath);

                if (DebugMode)
                {
                    console.WriteLine($"Temporary script @ {tempScriptPath}:");
                    console.WriteLine("---");
                    console.WriteLine(script);
                    console.WriteLine("---");
                }

                console.WriteLine();
                console.WriteLine("Executing generated script...");
                console.WriteLine();

                exitCode = ProcessHelper.RunProcess(
                    shellPath,
                    new[] { tempScriptPath },
                    opts.SourceDir,
                    (sender, args) =>
                    {
                        if (args.Data != null)
                        {
                            console.WriteLine(args.Data);
                        }
                    },
                    (sender, args) =>
                    {
                        if (args.Data != null)
                        {
                            console.Error.WriteLine(args.Data);
                        }
                    },
                    waitTimeForExit: null);
                timedEvent.AddProperty("exitCode", exitCode.ToString());
            }

            return exitCode;
        }

        internal override void ConfigureBuildScriptGeneratorOptions(BuildScriptGeneratorOptions options)
        {
            BuildScriptGeneratorOptionsHelper.ConfigureBuildScriptGeneratorOptions(options, sourceDir: SourceDir);
        }

        internal override IServiceProvider TryGetServiceProvider(IConsole console)
        {
            // Gather all the values supplied by the user in command line
            SourceDir = string.IsNullOrEmpty(SourceDir) ?
                Directory.GetCurrentDirectory() : Path.GetFullPath(SourceDir);

            // NOTE: Order of the following is important. So a command line provided value has higher precedence
            // than the value provided in a configuration file of the repo.
            var config = new ConfigurationBuilder()
                .AddIniFile(Path.Combine(SourceDir, Constants.BuildEnvironmentFileName), optional: true)
                .AddEnvironmentVariables()
                .Build();

            // Override the GetServiceProvider() call in CommandBase to pass the IConsole instance to
            // ServiceProviderBuilder and allow for writing to the console if needed during this command.
            var serviceProviderBuilder = new ServiceProviderBuilder(LogFilePath, console)
                .ConfigureServices(services =>
                {
                    // Configure Options related services
                    // We first add IConfiguration to DI so that option services like
                    // `DotNetCoreScriptGeneratorOptionsSetup` services can get it through DI and read from the config
                    // and set the options.
                    services
                        .AddSingleton<IConfiguration>(config)
                        .AddOptionsServices()
                        .Configure<BuildScriptGeneratorOptions>(options =>
                        {
                            // These values are not retrieve through the 'config' api since we do not expect
                            // them to be provided by an end user.
                            options.SourceDir = SourceDir;
                        });
                });

            return serviceProviderBuilder.Build();
        }
    }
}
