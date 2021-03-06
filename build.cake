// Install addins.
#addin "nuget:https://www.nuget.org/api/v2?package=Polly&version=4.2.0"
#addin "nuget:https://www.nuget.org/api/v2?package=Newtonsoft.Json&version=9.0.1"
#addin "nuget:https://www.nuget.org/api/v2?package=NuGet.Core&version=2.14"

// Install tools.
#tool "nuget:https://www.nuget.org/api/v2?package=GitVersion.CommandLine"
#tool "nuget:https://www.nuget.org/api/v2?package=NUnit.ConsoleRunner&version=3.4.0"
#tool "nuget:https://chocolatey.org/api/v2?package=gitlink&version=2.4.1"

// Load other scripts.
#load "./build/parameters.cake"

///////////////////////////////////////////////////////////////////////////////
// USINGS
///////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Polly;

//////////////////////////////////////////////////////////////////////
// PARAMETERS
//////////////////////////////////////////////////////////////////////

BuildParameters parameters = BuildParameters.GetParameters(Context);

///////////////////////////////////////////////////////////////////////////////
// SETUP / TEARDOWN
///////////////////////////////////////////////////////////////////////////////

Setup(context =>
{
    parameters.Initialize(context);

    Information("==============================================");
    Information("==============================================");

    if (parameters.IsRunningOnGoCD)
    {
        Information("Pipeline Name: " + BuildSystem.GoCD.Environment.Pipeline.Name + "{" + BuildSystem.GoCD.Environment.Pipeline.Counter + "}");
        Information("Stage Name: " + BuildSystem.GoCD.Environment.Stage.Name + "{" + BuildSystem.GoCD.Environment.Stage.Counter + "}");
    }

    Information("Solution: " + parameters.Solution);
    Information("Target: " + parameters.Target);
    Information("Configuration: " + parameters.Configuration);
    Information("IsLocalBuild: " + parameters.IsLocalBuild);
    Information("IsRunningOnUnix: " + parameters.IsRunningOnUnix);
    Information("IsRunningOnWindows: " + parameters.IsRunningOnWindows);
    Information("IsRunningOnGoCD: " + parameters.IsRunningOnGoCD);
    Information("IsRunningOnVSTS: " + parameters.IsRunningOnVSTS);
    Information("IsReleaseBuild: " + parameters.IsReleaseBuild);
    Information("ShouldPublish: " + parameters.ShouldPublish);

    // Increase verbosity?
    if(parameters.IsReleaseBuild && (context.Log.Verbosity != Verbosity.Diagnostic)) {
        Information("Increasing verbosity to diagnostic.");
        context.Log.Verbosity = Verbosity.Diagnostic;
    }

    Information("Building version {0} {5} of {4} ({1}, {2}) using version {3} of Cake",
        parameters.Version.SemVersion,
        parameters.Configuration,
        parameters.Target,
        parameters.Version.CakeVersion,
        parameters.Solution,
        parameters.Version.Sha.Substring(0,8));

});


///////////////////////////////////////////////////////////////////////////////
// TEARDOWN
///////////////////////////////////////////////////////////////////////////////

Teardown(context =>
{
    Information("Finished running tasks.");

    if(parameters.IsRunningOnVSTS) {
        var commands = context.TFBuild().Commands;
        if(!context.Successful)
            commands.WriteError(string.Format("Exception: {0} Message: {1}\nStack: {2}", context.ThrownException.GetType(), context.ThrownException.Message, context.ThrownException.StackTrace));
    }
    else if(!context.Successful)
    {
        Error(string.Format("Exception: {0} Message: {1}\nStack: {2}", context.ThrownException.GetType(), context.ThrownException.Message, context.ThrownException.StackTrace));
    }
});

//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

TaskSetup(setupContext =>
{
    if(parameters.IsRunningOnVSTS) {
        var commands = setupContext.TFBuild().Commands;
        commands.CreateNewRecord(setupContext.Task.Name, "build", 1);
    }
});

TaskTeardown(teardownContext =>
{
    if(parameters.IsRunningOnVSTS) {
        var commands = teardownContext.TFBuild().Commands;

        if(teardownContext.Skipped)
            commands.CompleteCurrentTask(TFBuildTaskResult.Skipped);
        else
            commands.CompleteCurrentTask(TFBuildTaskResult.Succeeded);
    }
});

//////////////////////////////////////////////////////////////////////
// DEFINITIONS
//////////////////////////////////////////////////////////////////////

Task("Clean")
    .Does(() =>
{
    CleanDirectories(parameters.Paths.Directories.ToClean);
});

Task("Restore-NuGet-Packages")
    .IsDependentOn("Clean")
    .Does(() =>
{
    var maxRetryCount = 10;
    Policy
        .Handle<Exception>()
        .Retry(maxRetryCount, (exception, retryCount, context) => {
            if (retryCount == maxRetryCount)
            {
                throw exception;
            }
            else
            {
                Verbose("{0}", exception);
            }})
        .Execute(()=> {
                NuGetRestore(parameters.Solution, new NuGetRestoreSettings {
                });
        });
});
Task("Update-NuGet-Packages")
    .IsDependentOn("Restore-NuGet-Packages")
    .Does(() =>
{
    var maxRetryCount = 10;
    Policy
        .Handle<Exception>()
        .Retry(maxRetryCount, (exception, retryCount, context) => {
            if (retryCount == maxRetryCount)
            {
                throw exception;
            }
            else
            {
                Verbose("{0}", exception);
            }})
        .Execute(()=> {
                // Update all our packages to latest build version
                NuGetUpdate(parameters.Solution, new NuGetUpdateSettings {
                    Safe = true,
                    ArgumentCustomization = args => args.Append("-FileConflictAction Overwrite")
                });
        });
});

Task("Build")
    .IsDependentOn("Restore-NuGet-Packages")
    .Does(() =>
{   
    if(IsRunningOnWindows())
    {
      // Use MSBuild
      MSBuild(parameters.Solution, settings => {
        settings.SetConfiguration(parameters.Configuration);
        settings.SetVerbosity(Verbosity.Minimal);
      });
    }
    else
    {
      // Use XBuild
      XBuild(parameters.Solution, settings => {
        settings.SetConfiguration(parameters.Configuration);
        settings.SetVerbosity(Verbosity.Minimal);
      });
    }
});

Task("Run-Unit-Tests")
    .IsDependentOn("Build")
    .Does(() =>
{
    NUnit3("./src/**/bin/" + parameters.Configuration + "/*.UnitTests.dll", new NUnit3Settings {
        NoResults = true
        });
});

Task("Copy-Files")
    .IsDependentOn("Run-Unit-Tests")
    .Does(() =>
{
    // GitLink
    if(parameters.IsRunningOnWindows)
    {
        Information("Updating PDB files using GitLink");
        GitLink(
            Context.Environment.WorkingDirectory.FullPath,
            new GitLinkSettings {

                SolutionFileName = parameters.Solution.FullPath,
                ShaHash = parameters.Version.Sha
            });
    }

    // Copy files from artifact sources to artifact directory
    foreach(var project in parameters.Paths.Files.Projects) 
    {
        CleanDirectory(parameters.Paths.Directories.ArtifactsBin.Combine(project.AssemblyName));
        CopyFiles(project.GetBinaries(),
            parameters.Paths.Directories.ArtifactsBin.Combine(project.AssemblyName));
    }
    // Copy license
    CopyFileToDirectory("./LICENSE", parameters.Paths.Directories.ArtifactsBin);
});

Task("Zip-Files")
    .IsDependentOn("Copy-Files")
    .Does(() =>
{
    var files = GetFiles( parameters.Paths.Directories.ArtifactsBin + "/**/*" );
    Zip(parameters.Paths.Directories.ArtifactsBin, parameters.Paths.Files.ZipBinaries, files);

	
});

Task("Create-NuGet-Packages")
    .IsDependentOn("Copy-Files")
    .Does(() =>
{
    // Build libraries
    foreach(var nuget in parameters.Packages.Nuget)
    {
        Information("Building nuget package: " + nuget.Id + " Version: " + nuget.Nuspec.Version);
        NuGetPack(nuget.Nuspec);
    }
});

Task("Upload-AppVeyor-Artifacts")
    .IsDependentOn("Zip-Files")
    .IsDependentOn("Create-NuGet-Packages")
    .WithCriteria(() => parameters.IsRunningOnAppVeyor)
    .Does(() =>
{
    AppVeyor.UploadArtifact(parameters.Paths.Files.ZipBinaries);
    foreach(var package in GetFiles(parameters.Paths.Directories.NugetRoot + "/*"))
    {
        AppVeyor.UploadArtifact(package);
    }
});

Task("Publish-NuGet")
    .IsDependentOn("Create-NuGet-Packages")
    .WithCriteria(() => parameters.ShouldPublish)
    .Does(() =>
{
    // Resolve the API key.
    var apiKey = EnvironmentVariable("NUGET_API_KEY");
    if(string.IsNullOrEmpty(apiKey) && !parameters.ShouldPublishToArtifactory) {
        throw new InvalidOperationException("Could not resolve NuGet API key.");
    }

    if(parameters.ShouldPublishToArtifactory){

        var username = parameters.Artifactory.UserName;
        var password = parameters.Artifactory.Password;

        if(string.IsNullOrEmpty(username) && parameters.IsLocalBuild)
        {
            Console.Write("Artifactory UserName: ");
            username = Console.ReadLine();
        }
        if(string.IsNullOrEmpty(password) && parameters.IsLocalBuild)
        {
            Console.Write("Artifactory Password: ");
            password = Console.ReadLine();
        }
        apiKey = string.Concat(username, ":", password);
    }

    // Resolve the API url.
    var apiUrl = EnvironmentVariable("NUGET_URL");
    if(string.IsNullOrEmpty(apiUrl)) {
        throw new InvalidOperationException("Could not resolve NuGet API url.");
    }

    foreach(var package in parameters.Packages.Nuget)
    {
		Information("Publish nuget: " + package.PackagePath);
        var packageDir = apiUrl;
        if(parameters.ShouldPublishToArtifactory)
            packageDir = string.Concat(apiUrl, "/", package.Id);

		var maxRetryCount = 10;
		Policy
			.Handle<Exception>()
			.Retry(maxRetryCount, (exception, retryCount, context) => {
				if (retryCount == maxRetryCount)
				{
					throw exception;
				}
				else
				{
					Verbose("{0}", exception);
				}})
			.Execute(()=> {

					// Push the package.
					NuGetPush(package.PackagePath, new NuGetPushSettings {
					  ApiKey = apiKey,
					  Source = packageDir
					});
			});
    }
});
Task("Create-GoCD-Artifacts")
    .IsDependentOn("Zip-Files")
    .WithCriteria(() => parameters.IsRunningOnGoCD)
    .Does(() =>
{
});
Task("Create-VSTS-Artifacts")
    .IsDependentOn("Zip-Files")
    .IsDependentOn("Create-NuGet-Packages")
    .WithCriteria(() => parameters.IsRunningOnVSTS)
    .Does(context =>
{
    var commands = context.BuildSystem().TFBuild.Commands;

    commands.UploadArtifact("source", context.Environment.WorkingDirectory + "/", "source");

    commands.AddBuildTag(parameters.Version.Sha);
    commands.AddBuildTag(parameters.Version.SemVersion);
});

//////////////////////////////////////////////////////////////////////
// TASK TARGETS
//////////////////////////////////////////////////////////////////////

Task("Package")
  .IsDependentOn("Zip-Files")
  .IsDependentOn("Create-NuGet-Packages");

Task("Default")
  .IsDependentOn("Package");

Task("AppVeyor")
  .IsDependentOn("Upload-AppVeyor-Artifacts")
  .IsDependentOn("Publish-NuGet");
Task("GoCD")
  .IsDependentOn("Create-GoCD-Artifacts")
  .IsDependentOn("Publish-NuGet");

Task("VSTS")
  .IsDependentOn("Create-VSTS-Artifacts");
Task("VSTS-Publish")
  .IsDependentOn("Publish-Nuget");

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////

RunTarget(parameters.Target);
