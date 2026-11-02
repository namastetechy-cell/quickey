using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Quickey.Core.Documents;

/// <summary>
/// The parts a Word document needs to open like one Word made: without settings Word opens it in
/// Compatibility Mode (old table layout rules), and without styles it falls back to Times New Roman.
/// </summary>
internal static class WordDefaults
{
    public static void Apply(MainDocumentPart main)
    {
        var settings = main.AddNewPart<DocumentSettingsPart>();
        settings.Settings = new DocumentFormat.OpenXml.Wordprocessing.Settings(new Compatibility(new CompatibilitySetting
        {
            Name = CompatSettingNameValues.CompatibilityMode,
            Uri = "http://schemas.microsoft.com/office/word",
            Val = "15", // Word 2013 and later
        }));

        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri", EastAsia = "Calibri" },
                    new FontSize { Val = "22" },
                    new FontSizeComplexScript { Val = "22" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "0", Line = "259", LineRule = LineSpacingRuleValues.Auto }))),
            new Style(new StyleName { Val = "Normal" }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });
    }

    /// <summary>Tight cell padding (about 1 mm), like Excel's, so narrow columns don't wrap their text.</summary>
    public static TableCellMarginDefault TightCellMargins() => new(
        new TopMargin { Width = "0", Type = TableWidthUnitValues.Dxa },
        new TableCellLeftMargin { Width = 57, Type = TableWidthValues.Dxa },
        new BottomMargin { Width = "0", Type = TableWidthUnitValues.Dxa },
        new TableCellRightMargin { Width = 57, Type = TableWidthValues.Dxa });
}
