using OptiumPlusFileManager.Api;
using OptiumPlusFileManager.Worker;

namespace OptiumPlusFileManager;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var stdioMode = args.Any(argument => argument.Equals("--mcp-stdio", StringComparison.OrdinalIgnoreCase));
        if (!stdioMode)
        {
            System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
            System.Windows.Forms.Application.ThreadException += (_, eventArgs) =>
            {
                var logPath = Path.Combine(Path.GetTempPath(), "OptiumPlusFileManager-crash.log");
                try
                {
                    File.AppendAllText(logPath,
                        $"{DateTimeOffset.Now:O}{Environment.NewLine}{eventArgs.Exception}{Environment.NewLine}{Environment.NewLine}");
                }
                catch (IOException) { }

                System.Windows.Forms.MessageBox.Show(
                    $"Произошла ошибка в интерфейсе приложения. Полный отчёт сохранён: {logPath}\n\n" +
                    $"{eventArgs.Exception.GetType().Name}: {eventArgs.Exception.Message}",
                    "Optium+ File Manager — ошибка", System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
                System.Windows.Forms.Application.Exit();
            };
        }

        FileSorterRuntime? runtime = null;
        try
        {
            runtime = new FileSorterRuntime();
            if (stdioMode)
            {
                runtime.InitializeAsync().GetAwaiter().GetResult();
                McpStdioServer.RunAsync(runtime).GetAwaiter().GetResult();
            }
            else
                System.Windows.Forms.Application.Run(new MainForm(runtime,
                    args.Any(argument => argument.Equals("--minimized", StringComparison.OrdinalIgnoreCase))));
        }
        catch (Exception exception)
        {
            if (stdioMode)
                Console.Error.WriteLine($"Optium+ File Manager MCP server failed: {exception.GetType().Name}: {exception.Message}");
            else
                System.Windows.Forms.MessageBox.Show($"Приложение не удалось запустить.\n{exception.Message}", "Optium+ File Manager",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
        finally
        {
            runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
