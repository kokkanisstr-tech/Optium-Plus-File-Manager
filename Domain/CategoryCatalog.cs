namespace OptiumPlusFileManager.Domain;

public sealed record CategoryDefinition(
    string Id,
    string DisplayName,
    string DirectoryName,
    IReadOnlyList<CategoryDefinition> Subcategories);

public static class CategoryCatalog
{
    public static IReadOnlyList<CategoryDefinition> Categories { get; } =
    [
        new("programming", "Программирование", "Programming", [
            new("csharp", "C#", "CSharp", []),
            new("python", "Python", "Python", []),
            new("java", "Java", "Java", []),
            new("javascript", "JavaScript / TypeScript", "JavaScript", []),
            new("sql", "SQL", "SQL", []),
            new("c", "C", "C", []),
            new("cpp", "C++", "Cpp", []),
            new("html", "HTML", "HTML", []),
            new("css", "CSS", "CSS", []),
            new("other-code", "Другой код", "OtherCode", [])
        ]),
        new("documents", "Документы", "Documents", [
            new("study", "Учёба", "Study", []),
            new("work", "Работа", "Work", []),
            new("finance", "Финансы", "Finance", []),
            new("other", "Прочее", "Other", [])
        ]),
        new("images", "Изображения", "Images", [
            new("photo", "Фото", "Photos", []),
            new("screenshot", "Скриншоты", "Screenshots", []),
            new("scan", "Сканы документов", "Scans", []),
            new("diagram", "Схемы и диаграммы", "Diagrams", []),
            new("illustration", "Иллюстрации", "Illustrations", []),
            new("other", "Другие изображения", "Other", [])
        ]),
        new("audio", "Аудио", "Audio", []),
        new("video", "Видео", "Video", []),
        new("archives", "Архивы", "Archives", []),
        new("applications", "Программы", "Applications", []),
        new("other", "Прочее", "Other", [])
    ];

    public static bool IsAllowed(string categoryId, string subcategoryId)
    {
        var category = Categories.FirstOrDefault(item =>
            string.Equals(item.Id, categoryId, StringComparison.OrdinalIgnoreCase));
        if (category is null)
            return false;
        return category.Subcategories.Count == 0
            ? string.Equals(subcategoryId, "other", StringComparison.OrdinalIgnoreCase)
            : category.Subcategories.Any(item => string.Equals(item.Id, subcategoryId, StringComparison.OrdinalIgnoreCase));
    }

    public static (string Category, string? Subcategory) GetPath(string categoryId, string subcategoryId)
    {
        if (!IsAllowed(categoryId, subcategoryId))
            throw new ArgumentException("Категория отсутствует в разрешённом справочнике.");

        var category = Categories.First(item => string.Equals(item.Id, categoryId, StringComparison.OrdinalIgnoreCase));
        if (category.Subcategories.Count == 0)
            return (category.DirectoryName, null);

        var subcategory = category.Subcategories.First(item =>
            string.Equals(item.Id, subcategoryId, StringComparison.OrdinalIgnoreCase));
        return (category.DirectoryName, subcategory.DirectoryName);
    }

    public static IEnumerable<(string CategoryId, string SubcategoryId, string DisplayName)> AllChoices() =>
        Categories.SelectMany(category => category.Subcategories.Count == 0
            ? [(category.Id, "other", category.DisplayName)]
            : category.Subcategories.Select(subcategory =>
                (category.Id, subcategory.Id, $"{category.DisplayName} / {subcategory.DisplayName}")));
}
