namespace OptiumPlusFileManager.Application;

public sealed class AppSettings
{
    public List<string> WatchFolders { get; set; } = [];
    public List<string> ExcludedPaths { get; set; } = [];
    public List<string> ExcludedPatterns { get; set; } = [];
    public List<string> LocalOnlyPaths { get; set; } = [];
    public List<string> AiAllowedFileTypes { get; set; } = ["text", "pdf", "docx", "xlsx", "pptx"];
    public bool AllowAiImageAnalysis { get; set; }
    public bool OpenDestinationFolderAfterSort { get; set; } = true;
    public string SourceFolder { get; set; } = string.Empty;
    public string DestinationFolder { get; set; } = string.Empty;
    public bool AutomaticSorting { get; set; }
    public bool WatchSubdirectories { get; set; }
    public bool StartWithWindows { get; set; }
    public bool DarkTheme { get; set; } = true;
    public int MaxFileSizeMb { get; set; } = 512;
    public int MonthlyAiRequestLimit { get; set; } = 100;
    public string AiProviderMode { get; set; } = "Off";
    public string OpenAiModel { get; set; } = "gpt-5.6-terra";
    public string GeminiModel { get; set; } = "gemini-3.6-flash";
    public string ClaudeModel { get; set; } = "claude-sonnet-5-5";
    public double AutoSortConfidence { get; set; } = 0.85;
    public double ReviewConfidence { get; set; } = 0.60;
}
