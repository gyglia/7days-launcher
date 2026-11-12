using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

partial class Installer
{
    static void ValidateSchedule(ReleaseManifest manifest)
    {
        if (String.IsNullOrEmpty(manifest.ActivationMode) || manifest.ActivationMode == "immediate") {
            if (!String.IsNullOrEmpty(manifest.ActivateAt)) throw new IOException("У срочного релиза не должно быть даты активации.");
            return;
        }
        DateTimeOffset date;
        if (manifest.ActivationMode != "scheduled" || manifest.Schema != 2 ||
            !DateTimeOffset.TryParseExact(manifest.ActivateAt,"yyyy-MM-dd'T'HH:mm:sszzz",CultureInfo.InvariantCulture,DateTimeStyles.None,out date) ||
            date.Offset != TimeSpan.FromHours(3) || date.TimeOfDay != TimeSpan.FromHours(4) ||
            !Regex.IsMatch(manifest.PreviousManifestUrl ?? "", "^" + Regex.Escape(ReleaseRoot) + @"download/[A-Za-z0-9._-]+/manifest\.json$"))
            throw new IOException("Плановый релиз должен содержать конкретную дату 04:00:00+03:00 и ссылку на предыдущий манифест.");
    }

    static bool ReleaseIsDue(ReleaseManifest release, DateTimeOffset now)
    {
        ValidateSchedule(release);
        return release.ActivationMode != "scheduled" || now >= DateTimeOffset.ParseExact(release.ActivateAt,"yyyy-MM-dd'T'HH:mm:sszzz",CultureInfo.InvariantCulture);
    }

    static ReleaseManifest ResolveScheduledRelease(ReleaseManifest release, DateTimeOffset now, Func<string,string> fetch)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (!ReleaseIsDue(release,now)) {
            if (seen.Count >= 32 || !seen.Add(release.PreviousManifestUrl)) throw new IOException("Цикл в расписании релизов.");
            release = ParseManifest(fetch(release.PreviousManifestUrl));
        }
        return release;
    }

    sealed partial class LauncherWindow
    {
        ReleaseManifest scheduledRelease;
        string ScheduleCache { get { return Path.Combine(appDir,"schedule-manifest.json"); } }

        Tuple<ReleaseManifest,bool,ReleaseManifest> LoadScheduledRelease()
        {
            bool noNetwork = false; string text;
            try { text = FetchManifest(); ParseManifest(text); SaveText(ScheduleCache,text); }
            catch(WebException) {
                noNetwork = true;
                text = File.Exists(ScheduleCache) ? File.ReadAllText(ScheduleCache) : File.Exists(CachedManifest) ? File.ReadAllText(CachedManifest) : ResourceText("manifest.json");
            }
            var latest = ParseManifest(text);
            var now = DateTimeOffset.UtcNow;
            var selected = ResolveScheduledRelease(latest,now,url => {
                string tag = new Uri(url).Segments[new Uri(url).Segments.Length - 2].TrimEnd('/');
                string cache = Path.Combine(appDir,"release-cache",tag + ".json");
                try { string prior = FetchManifestAt(url); ParseManifest(prior); SaveText(cache,prior); return prior; }
                catch(WebException) { noNetwork = true; if (File.Exists(cache)) return File.ReadAllText(cache); throw; }
            });
            SaveText(CachedManifest,Json.Serialize(selected));
            return Tuple.Create(selected,noNetwork,ReleaseIsDue(latest,now) ? null : latest);
        }

        void StartScheduleTimer()
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            timer.Tick += delegate {
                if (!disposed && !busy && !launching && scheduledRelease != null && !GameRunning() && ReleaseIsDue(scheduledRelease,DateTimeOffset.UtcNow)) CheckRelease();
            };
            timers.Add(timer); timer.Start();
        }
    }

    static int TestSchedule(string log)
    {
        try {
            var old = ParseManifest(ResourceText("manifest.json"));
            var next = ParseManifest(Json.Serialize(old)); next.Schema = 2; next.Version = "next";
            next.ActivationMode = "scheduled"; next.ActivateAt = "2026-11-13T04:00:00+03:00";
            next.PreviousManifestUrl = ReleaseRoot + "download/previous/manifest.json";
            var before = DateTimeOffset.Parse("2026-11-13T00:59:59Z",CultureInfo.InvariantCulture);
            var exact = before.AddSeconds(1);
            Require(ResolveScheduledRelease(next,before,url => Json.Serialize(old)).Version == old.Version,"Old build before 04:00 Moscow, including fresh install");
            Require(ResolveScheduledRelease(next,exact,url => { throw new Exception("Unneeded fetch"); }).Version == "next","New build at exactly 04:00 Moscow");
            Require(ReleaseIsDue(next,exact.ToOffset(TimeSpan.FromHours(-8))),"Client timezone does not change activation");
            Require(ReleaseIsDue(next,exact.AddDays(1)),"Missed date activates on next start");
            var later = ParseManifest(Json.Serialize(next)); later.Version = "later"; later.ActivateAt = "2026-11-14T04:00:00+03:00";
            later.PreviousManifestUrl = ReleaseRoot + "download/next/manifest.json";
            Require(ResolveScheduledRelease(later,before,url => url.EndsWith("/next/manifest.json") ? Json.Serialize(next) : Json.Serialize(old)).Version == old.Version,"Multiple queued releases resolve to currently active build");
            bool rejected = false; try { ResolveScheduledRelease(next,before,url => Json.Serialize(next)); } catch(IOException) { rejected = true; } Require(rejected,"Cycle rejected");
            next.ActivateAt = "2026-11-13T04:00:00Z"; rejected = false; try { ParseManifest(Json.Serialize(next)); } catch(IOException) { rejected = true; } Require(rejected,"Wrong offset rejected");
            next.ActivateAt = "2026-11-13T05:00:00+03:00"; rejected = false; try { ParseManifest(Json.Serialize(next)); } catch(IOException) { rejected = true; } Require(rejected,"Wrong Moscow hour rejected");
            next.ActivationMode = "immediate"; next.ActivateAt = null; Require(ReleaseIsDue(next,before),"Emergency release immediate");
            Require(ReleaseIsDue(old,before),"Legacy manifest remains immediate");
            File.WriteAllText(log,"PASS: before/exactly/after 04:00 Moscow, timezone independence, prior build for fresh installs, queued releases, cycle and invalid schedule rejection, immediate and legacy releases."); return 0;
        } catch(Exception error) { File.WriteAllText(log,error.ToString()); return 1; }
    }
}
