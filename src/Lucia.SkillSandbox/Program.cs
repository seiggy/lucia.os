using Lucia.SkillSandbox;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
var exit = await SandboxMain.RunAsync(Console.In, Console.Out);
// A guest that ignores cancellation must not keep the process, and so the container, alive.
Environment.Exit(exit);
return exit;
