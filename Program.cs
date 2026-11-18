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
        }

        FileSorterRuntime? runtime = null;
        try
        {
            runtime = new FileSorterRuntime();
            runtime.InitializeAsync().GetAwaiter().GetResult();
            if (stdioMode)
                McpStdioServer.RunAsync(runtime).GetAwaiter().GetResult();
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
