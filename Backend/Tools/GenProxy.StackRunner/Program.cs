using GenProxy.StackRunner;

return BuildProcess.IsLauncher(args)
    ? await BuildProcess.RunLauncherAsync(args)
    : await StackRunnerApplication.RunAsync(args);
