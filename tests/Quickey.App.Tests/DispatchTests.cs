using Quickey.App.Office;

namespace Quickey.App.Tests;

public class DispatchTests
{
    public class OldDocument
    {
        public string? Saved { get; private set; }
        public void SaveAs(string path, int format) => Saved = $"{path}|{format}";
    }

    public class NewDocument
    {
        public string? Saved { get; private set; }
        public void SaveAs2(string path, int format) => Saved = $"2:{path}|{format}";
        public void SaveAs(string path, int format) => Saved = $"1:{path}|{format}";
    }

    [Fact]
    public void Calls_the_newest_method_that_exists()
    {
        var modern = new NewDocument();
        Dispatch.CallFirst(modern, ["SaveAs2", "SaveAs"], @"C:\out.docx", 16);
        Assert.Equal(@"2:C:\out.docx|16", modern.Saved);

        var old = new OldDocument();
        Dispatch.CallFirst(old, ["SaveAs2", "SaveAs"], @"C:\out.docx", 16);
        Assert.Equal(@"C:\out.docx|16", old.Saved);
    }

    [Fact]
    public void Reports_when_no_method_exists() =>
        Assert.ThrowsAny<Exception>(() => Dispatch.CallFirst(new object(), ["SaveAs2", "SaveAs"], "x", 1));
}
