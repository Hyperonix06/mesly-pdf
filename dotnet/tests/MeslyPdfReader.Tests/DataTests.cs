using MeslyPdfReader.Data;
using Xunit;

namespace MeslyPdfReader.Tests;

public class DataTests
{
    private static AppDatabase NewDb() => new(Path.Combine(Fx.TempDir(), "mesly.db"));

    [Fact]
    public void SettingsRoundTrip()
    {
        var db = NewDb();
        var s = new AppSettings(db);
        Assert.Equal(ThemeChoice.System, s.Theme);
        string? changed = null;
        s.Changed += k => changed = k;
        s.Theme = ThemeChoice.Dark;
        s.DefaultZoom = 1.75;
        s.DarkReading = true;
        s.RecentLimit = 500; // sınırlandırılmalı
        Assert.Equal("files.recentLimit", changed);
        var s2 = new AppSettings(new AppDatabase(db.FilePath));
        Assert.Equal(ThemeChoice.Dark, s2.Theme);
        Assert.Equal(1.75, s2.DefaultZoom);
        Assert.True(s2.DarkReading);
        Assert.Equal(100, s2.RecentLimit);
    }

    [Fact]
    public void RecentFilesOrderPinAndClear()
    {
        var db = NewDb();
        db.AddRecent(@"C:\Belgeler\a.pdf", 3, 100);
        Thread.Sleep(5);
        db.AddRecent(@"C:\Belgeler\Çalışma Planı.pdf", 10, 2000);
        var list = db.GetRecent();
        Assert.Equal("Çalışma Planı.pdf", list[0].Name);
        db.SetPinned(@"C:\Belgeler\a.pdf", true);
        Assert.Equal("a.pdf", db.GetRecent()[0].Name);
        db.AddRecent(@"c:\belgeler\A.PDF", 4, 120); // büyük/küçük harf duyarsız yol
        Assert.Equal(2, db.GetRecent().Count);
        db.ClearRecent();
        Assert.Single(db.GetRecent()); // sabitlenmiş kalır
        db.RemoveRecent(@"C:\Belgeler\a.pdf");
        Assert.Empty(db.GetRecent());
    }

    [Fact]
    public void DocumentStateRemembersLastPage()
    {
        var db = NewDb();
        Assert.Null(db.GetDocumentState("x.pdf"));
        db.SaveDocumentState("x.pdf", new DocumentState(41, 1.25, "Custom"));
        db.SaveDocumentState("x.pdf", new DocumentState(42, 1.5, "FitWidth"));
        var st = db.GetDocumentState("x.pdf");
        Assert.Equal(42, st!.Page);
        Assert.Equal(1.5, st.Zoom);
        Assert.Equal("FitWidth", st.ZoomMode);
    }

    [Fact]
    public void TaskHistory()
    {
        var db = NewDb();
        var id = db.StartTask("merge", "a.pdf; b.pdf");
        db.FinishTask(id, "ok", "birlesik.pdf", null);
        var id2 = db.StartTask("split", "c.pdf");
        db.FinishTask(id2, "error", null, "Dosya bozuk");
        var tasks = db.GetTasks();
        Assert.Equal(2, tasks.Count);
        Assert.Equal("error", tasks[0].Status);
        Assert.Equal("Dosya bozuk", tasks[0].Message);
        Assert.Equal("birlesik.pdf", tasks[1].Output);
        Assert.NotNull(tasks[1].Finished);
    }

    [Fact]
    public void ConcurrentAccessIsSafe()
    {
        var db = NewDb();
        Parallel.For(0, 200, i =>
        {
            db.SetSetting($"k{i % 10}", i.ToString());
            db.AddRecent($"/tmp/f{i % 25}.pdf", i, i);
            _ = db.GetRecent(5);
        });
        Assert.Equal(25, db.GetRecent(100).Count);
    }
}
