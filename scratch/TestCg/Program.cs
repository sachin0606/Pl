using System;
using System.IO;
using System.Linq;
using BroadcastPlayout.Models;
using BroadcastPlayout.Services;

class Program
{
    [STAThread]
    static void Main()
    {
        Console.WriteLine("Refreshing all CG Demos...");
        var projects = CgDemoFactory.CreateDefaults(forceRefresh: true);
        Console.WriteLine($"Generated {projects.Count} demos.");

        var catDemo = projects.FirstOrDefault(x => x.Name == CgUniqueDemoFactory.CategoryTickerDemoName)
            ?? CgUniqueDemoFactory.Create().FirstOrDefault(x => x.Name == CgUniqueDemoFactory.CategoryTickerDemoName);

        if (catDemo != null)
        {
            Console.WriteLine($"Category Demo Duration: {catDemo.DurationSeconds}s");
            Console.WriteLine($"Data Sources: {catDemo.DataSources.Count}");
            foreach (var ds in catDemo.DataSources)
            {
                Console.WriteLine($"  DS: Name='{ds.Name}', Source='{ds.Source}'");
                Console.WriteLine($"  CachedItems: {ds.CachedItemsJson[..Math.Min(120, ds.CachedItemsJson.Length)]}...");
                var readRows = CgDataSourceService.ReadCachedRows(ds);
                Console.WriteLine($"  ReadCachedRows count: {readRows.Count}");
                var getRows = CgDataRuntime.Shared.GetRows(ds);
                Console.WriteLine($"  GetRows count: {getRows.Count}");
                if (getRows.Count > 0)
                {
                    var sched = CgDataSourceService.BuildCategoryFeedSchedule(catDemo, catDemo.Layers.First(x => x.Type == "Ticker"), getRows);
                    Console.WriteLine($"  Schedule TotalCycleDuration: {sched.TotalCycleDuration}s, Categories: {sched.Categories.Count}");
                    foreach (var c in sched.Categories)
                    {
                        Console.WriteLine($"    Cat: '{c.Category}', Items: {c.Items.Count}, EffDur: {c.EffectiveDuration:F2}s, Start: {c.StartTime:F2}s, End: {c.EndTime:F2}s");
                    }
                }
            }
            foreach (var l in catDemo.Layers)
            {
                Console.WriteLine($"  Layer: '{l.Name}' Type={l.Type} DataField='{l.DataField}' ItemDur={l.DataItemDurationSeconds} Start={l.StartSeconds} End={l.EndSeconds} In={l.AnimationIn}({l.AnimationInSeconds}s) Out={l.AnimationOut}({l.AnimationOutSeconds}s)");
            }

            var baseDir = @"c:\Users\karnk\Downloads\Kashtrix-COMPLETE-PROJECT-FIX49H-FULL-BUILD-DEBUG-RECOVERY-FINAL\Kashtrix-FIX49H";
            var p1 = Path.Combine(baseDir, "cg-demo", "News · Category Animated Ticker.kcg");
            var p2 = Path.Combine(baseDir, "demos", "News · Category Animated Ticker.kcg");

            KashtrixCgFileService.SaveComposition(p1, catDemo);
            KashtrixCgFileService.SaveComposition(p2, catDemo);
            Console.WriteLine($"Saved to:\n  {p1}\n  {p2}");

            // Verify loading back
            var loaded = KashtrixCgFileService.LoadComposition(p1);
            Console.WriteLine($"Loaded back verification: Name='{loaded.Name}', Duration={loaded.DurationSeconds}s, Layers={loaded.Layers.Count}");
        }
    }
}
