// MyRPA Runner: executes jobs assigned by the MyRPA Server (ADR-0049). Ctrl+C and service stop go through the host lifetime.
return await MyRPA.Runner.RunnerApplication.RunAsync(args, Console.Out, Console.Error, CancellationToken.None);
