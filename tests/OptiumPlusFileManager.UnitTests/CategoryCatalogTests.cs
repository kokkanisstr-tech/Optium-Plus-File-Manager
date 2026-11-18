using OptiumPlusFileManager.Application;
using OptiumPlusFileManager.Domain;

namespace OptiumPlusFileManager.UnitTests;

public sealed class CategoryCatalogTests
{
    [Theory]
    [InlineData("programming", "csharp")]
    [InlineData("programming", "sql")]
    [InlineData("programming", "cpp")]
    [InlineData("documents", "study")]
    [InlineData("images", "other")]
    [InlineData("images", "photo")]
    public void Allows_categories_from_the_fixed_catalog(string categoryId, string subcategoryId)
    {
        Assert.True(CategoryCatalog.IsAllowed(categoryId, subcategoryId));
    }

    [Theory]
    [InlineData("unknown", "other")]
    [InlineData("programming", "../../outside")]
    [InlineData("documents", "csharp")]
    public void Rejects_unknown_or_cross_category_choices(string categoryId, string subcategoryId)
    {
        Assert.False(CategoryCatalog.IsAllowed(categoryId, subcategoryId));
    }

    [Fact]
    public void Every_catalog_choice_maps_to_a_safe_relative_directory()
    {
        foreach (var (categoryId, subcategoryId, _) in CategoryCatalog.AllChoices())
        {
            var (categoryPath, subcategoryPath) = CategoryCatalog.GetPath(categoryId, subcategoryId);

            Assert.False(Path.IsPathRooted(categoryPath));
            Assert.DoesNotContain("..", categoryPath, StringComparison.Ordinal);
            if (subcategoryPath is not null)
            {
                Assert.False(Path.IsPathRooted(subcategoryPath));
                Assert.DoesNotContain("..", subcategoryPath, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData("download.part")]
    [InlineData("copy.crdownload")]
    [InlineData("partial.tmp")]
    [InlineData("transfer.download")]
    public void Recognizes_incomplete_download_suffixes(string fileName)
    {
        Assert.True(FileTypeClassifier.IsTemporary(fileName));
    }

    [Fact]
    public void Does_not_treat_normal_files_as_incomplete_downloads()
    {
        Assert.False(FileTypeClassifier.IsTemporary("assignment.txt"));
        Assert.False(FileTypeClassifier.IsTemporary("source.cs"));
    }
}
