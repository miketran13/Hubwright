using Hubwright.Cli;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Commands.cli = new Args(Environment.GetCommandLineArgs().Skip(1));
Commands.command = Commands.cli.At(0)?.ToLowerInvariant();
Commands.tenant = Commands.cli.Option("tenant");
Commands.self = Environment.ProcessPath;
return await Commands.RunAsync();
