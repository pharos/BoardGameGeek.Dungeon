#load nuget:?package=Cake.Dungeon&prerelease

Build.SetParameters
(
    title: "BoardGameGeek.Dungeon",

    defaultLog: true,

    runBuildSolutions: true,
    runDockerBuild: true,
    runUnitTests: true,
    runTestCoverageReports: true,
    runPublishToDocker: true,

    sourceDirectory: Build.Directories.Root,

    unitTestProjectPatterns: new[] { "Tests/*.csproj" },
    unitTestLoggers: new[] { "console;verbosity=detailed", "junit" },

    buildEmbedAllSources: true,
    dockerBuildPull: true,
    dockerPushLatest: true,
    dockerPushSkipDuplicate: true,

    dockerImages: new[]
    {
        new DockerImage
        {
            Repository = "dockfool/boardgamegeek-dungeon",
            Context = "Cli"
        }
    }
);

Build.Run();
