using System;
using Websocket.Client;
using System.Threading;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows.Forms;
using System.IO;
using NAudio.Wave;
using System.Globalization;

namespace Osu_Progress
{
    class Program
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AllocConsole();
        private static readonly ManualResetEvent ExitEvent = new ManualResetEvent(false);
        private static int[] saved_combo = new int[2]{0,0};
        private static int old_state = 5;
        private static int state;
        private static string songFolder;
        private static Dictionary<string, object> deserializedSettings = new Dictionary<string, object>();

        private static float Multiplier;
        private static bool requireFC;
        static void Main(string[] args)
        {
            AllocConsole();
            Directory.SetCurrentDirectory(Application.StartupPath);
            string settings;
            using (StreamReader sr = new StreamReader(File.Open("settings.json", FileMode.OpenOrCreate))) 
            {
                settings = sr.ReadToEnd();
            }
            if (string.IsNullOrEmpty(settings)) 
            {
                deserializedSettings = initialSetup();
            } else {
                deserializedSettings = JsonSerializer.Deserialize<Dictionary<string, object>>(settings);
            }
            Multiplier = float.Parse(deserializedSettings["multiplier"].ToString(), CultureInfo.InvariantCulture);
            requireFC = bool.Parse(deserializedSettings["require_FC"].ToString());

            var url = new Uri("ws://localhost:24050/ws");

            using (var client = new WebsocketClient(url))
            {
                client.Name = "gosumemoryClient";
                client.ReconnectTimeout = TimeSpan.FromSeconds(30);
                client.ErrorReconnectTimeout = TimeSpan.FromSeconds(30);

                client.MessageReceived.Subscribe(msg =>
                {
                    onMessage(msg);
                });

                client.Start().Wait();

                ExitEvent.WaitOne();
            }
        }
        static Dictionary<string, object> initialSetup() 
        {
            AllocConsole();
            var deserializedSettings = new Dictionary<string, object>();
            float multiplier;
            while (true) 
            {
                Console.WriteLine("Input the Combo increase multiplier for combo on objective completion: ");
                var input = Console.ReadLine();
                if (float.TryParse(input, out multiplier)) 
                {
                    break;
                }
            }
            bool? require_FC = null;
            while(true) 
            {
                Console.WriteLine("Would you want to enable objective completion on FC? (if no, then only a pass will be necessary) Y/n :");
                var input = Console.ReadLine();
                switch(input.ToLower()) 
                {
                    case "y":
                        require_FC = true;
                        break;
                    case "n":
                        require_FC = false;
                        break;
                }
                if (require_FC != null) 
                {
                    break;
                }
            }
            deserializedSettings.Add("multiplier", multiplier);
            deserializedSettings.Add("require_FC",require_FC);
            var serializedSettings = JsonSerializer.Serialize(deserializedSettings);

            File.WriteAllText("settings.json", serializedSettings);
            return deserializedSettings;
        }
        static void onMessage(ResponseMessage msg) 
        {
            var data = toTrueDictionary(msg.Text);
            var gameplay = toTrueDictionary(data["gameplay"].ToString());
            var gameplay_combo = toTrueDictionary(gameplay["combo"].ToString());
            int current_combo = int.Parse(gameplay_combo["current"].ToString());

            var menu = toTrueDictionary(data["menu"].ToString());
            state = int.Parse(menu["state"].ToString());
            
            var bm = toTrueDictionary(menu["bm"].ToString());
            var time = toTrueDictionary(bm["time"].ToString());
            int time_firstObj = int.Parse(time["firstObj"].ToString());
            int time_current = int.Parse(time["current"].ToString());
            int time_full = int.Parse(time["full"].ToString());

            var gameplay_hits = toTrueDictionary(gameplay["hits"].ToString());
            int hits_miss = int.Parse(gameplay_hits["0"].ToString());
            int hits_sliderBreaks = int.Parse(gameplay_hits["sliderBreaks"].ToString());

            var mods = toTrueDictionary(menu["mods"].ToString());
            string active_mods = mods["str"].ToString();

            var stats = toTrueDictionary(bm["stats"].ToString());
            int stats_maxCombo = int.Parse(stats["maxCombo"].ToString());

            var settings = toTrueDictionary(data["settings"].ToString());
            var folders = toTrueDictionary(settings["folders"].ToString());
            songFolder = folders["songs"].ToString();
            var path = toTrueDictionary(bm["path"].ToString());
            var file = path["file"].ToString();
            var bg = path["bg"].ToString();
            string mapFolder = Path.Combine(songFolder, path["folder"].ToString());
            string mapPath = Path.Combine(mapFolder, file);
            string mapFileName = file;
            string songPath = Path.Combine(mapFolder, path["audio"].ToString());

            // if the max combo acquired by the user during a play increase, then increase actual combo and max combo
            if (hasMaxComboChanged(current_combo)) 
            {
                saved_combo[1] = current_combo;
                Console.WriteLine(current_combo);
            }
            // if the user missed or broke, reset the combo and display "Miss or Break" in console
            if (hasMissedOrBroke(current_combo)) 
            {
                Console.WriteLine("Miss or Break");
            }
            if (hasPassed(active_mods, time_current, time_full)) 
            {
                Console.WriteLine("Map has been Passed");
                if (!bool.Parse(deserializedSettings["require_FC"].ToString())) 
                {
                    MakeMap(mapFileName, mapPath, songPath, mapFolder , bg, Multiplier);
                }
            }
            if (hasFCed(active_mods, time_current, time_full, hits_sliderBreaks, hits_miss)) 
            {
                Console.WriteLine("Map has been FCed");
                if (bool.Parse(deserializedSettings["require_FC"].ToString())) 
                {   
                    MakeMap(mapFileName, mapPath, songPath, mapFolder, bg, Multiplier);
                }
                saved_combo[0] = 0;
                saved_combo[1] = 0;
            }
            if (!isPlayingOrWatching(time_firstObj, time_current)) 
            {
                saved_combo[0] = 0;
                saved_combo[1] = 0;
            }
            old_state = state;
            saved_combo[0] = current_combo;
        }
        static void MakeMap(string mapFileName, string mapPath, string songPath, string mapFolder, string bg, float multiplier) 
        {   
            Console.WriteLine("Making map...");
            string newMapPath = Path.Combine(songFolder, "Osu!Progress - "+mapFileName.Replace(".osu", ""));
            if (!Directory.Exists(newMapPath)) 
            {
                Directory.CreateDirectory(newMapPath);
            }
            File.Copy(Path.Combine(mapFolder, bg), Path.Combine(Path.Combine(songFolder, "Osu!Progress - "+mapFileName.Replace(".osu", "")),$"{bg}"),true);

            List<string> map = File.ReadAllLines(mapPath).ToList();
            // header from 0 to hitObject property
            List<string> header = map.GetRange(0, getPropertyIndex(map, "HitObjects")+1);
            header = prepareBeatmapHeader(header, songFolder, mapFileName);

            int mapDuration = getLastNotetime(map);

            MakeMP3(multiplier, mapDuration, songPath, Path.Combine(Path.Combine(songFolder, "Osu!Progress - "+mapFileName.Replace(".osu", "")), $"level{getNextLevel(songFolder, mapFileName)}.mp3"));
            int newMP3Duration = getSongDuration(Path.Combine(Path.Combine(songFolder, "Osu!Progress - "+mapFileName.Replace(".osu", "")), $"level{getNextLevel(songFolder, mapFileName)}.mp3")); 

            List<string> newMap = new List<string>();
            int sectionStart = 0;
            for (float i = multiplier; i >= 1; i--) 
            {
                Console.WriteLine(sectionStart);
                if (i == 0) 
                {
                    int sectionDuration = getSectionDuration(songPath, getSongDuration(songPath));
                    List<string> section = map.GetRange(getPropertyIndex(map, "HitObjects") + 1, (map.Count - 1) - getPropertyIndex(map, "HitObjects"));
                    newMap.AddRange(setTimings(section, sectionStart));
                    sectionStart += sectionDuration;
                }
                if (i-1 < 1 & i != 0) 
                {
                    int sectionDuration = 0;
                    List<string> section = map.GetRange(getPropertyIndex(map, "HitObjects") + 1, (map.Count - 1) - getPropertyIndex(map, "HitObjects"));
                    for (int j = section.Count-1; j >= 0; j--) 
                    {
                        if (!string.IsNullOrEmpty(section[j])) 
                        {
                            if (!((section[j].Split(",")[3] == "5") | (section[j].Split(",")[3] == "1")))
                            {
                                section.RemoveAt(j);
                            }
                            else 
                            {
                                sectionDuration = getSectionDuration(songPath, int.Parse(section[j].Split(",")[2])+3000);
                                break;
                            }
                        }
                    }
                    newMap.AddRange(setTimings(section, sectionStart));
                    sectionStart += sectionDuration;
                    break;
                }
                else
                {
                    int sectionDuration = 0;
                    List<string> section = map.GetRange(getPropertyIndex(map, "HitObjects") + 1, (map.Count - 1) - getPropertyIndex(map, "HitObjects"));
                    for (int j = section.Count-1; j >= 0; j--) 
                    {
                        if (!string.IsNullOrEmpty(section[j])) 
                        {
                            if (!(section[j].Split(",")[3] == "5") | !(section[j].Split(",")[3] == "1"))
                            {
                                section.RemoveAt(j);
                            }
                            else 
                            {
                                sectionDuration = getSectionDuration(songPath, int.Parse(section[j].Split(",")[2]));
                                break;
                            }
                        }
                    }
                    newMap.AddRange(setTimings(section, sectionStart));
                    sectionStart += sectionDuration;
                    break;      
                }
            }
            Console.WriteLine(sectionStart);
            if (multiplier > 0) {
                int sectionDuration = (int)(getSectionDuration(songPath, getSongDuration(songPath)) * 0.5);
                Console.WriteLine($"sectionDuration = {sectionDuration}");
                List<string> section = map.GetRange(getPropertyIndex(map, "HitObjects") + 1, (map.Count - 1) - getPropertyIndex(map, "HitObjects"));
                for (int j = section.Count-1; j >= 0; j--) {
                    if (!(int.Parse(section[j].Split(",")[2]) > sectionDuration)) {
                        section = section.GetRange(j+1,(section.Count-1)-j);
                        break;
                    }
                    string[] line = section[j].Split(",");
                    if (line[3] == "12") 
                    {
                        line[2] = $"{newMP3Duration - (getSongDuration(songPath) - int.Parse(line[2]))}";
                        line[5] = $"{newMP3Duration - (getSongDuration(songPath) - int.Parse(line[5]))}";
                    }
                    else
                    {
                        line[2] = $"{newMP3Duration - (getSongDuration(songPath) - int.Parse(line[2]))}";
                    }
                    section[j] = "";
                    for (int k = 0; k != line.Length; k++) 
                    {   
                        section[j] += line[k];
                        if (k != line.Length-1) 
                        {
                        section[j] += ",";
                        }
                    } 
                }
                newMap.AddRange(section);
            }
            using (TextWriter tw = new StreamWriter(Path.Combine(Path.Combine(songFolder, "Osu!Progress - "+mapFileName.Replace(".osu", "")), $"Osu!Progress - {mapFileName.Split(".osu")[0]} - level {getNextLevel(songFolder, mapFileName)}.osu"))) 
            {
                foreach (String line in header)
                    tw.WriteLine(line);
                foreach (String line in newMap)
                    tw.WriteLine(line);
            }
            Console.WriteLine("Done");
        }
        static Dictionary<string, object> toTrueDictionary(string stringtoconvert) 
        {
            Dictionary<string, object> dictionary = JsonSerializer.Deserialize<Dictionary<string, object>>(stringtoconvert);
            return dictionary;
        }
        static Boolean hasMaxComboChanged(int current_combo) 
        {
            if (current_combo > saved_combo[0] & current_combo > saved_combo[1] & state == 2) 
            {
                return true;
            }
            return false;
        }
        static Boolean hasMissedOrBroke(int current_combo) 
        {
            if (current_combo == 0 & saved_combo[0] > current_combo & state == 2 & old_state == 2) 
            {
                return true;
            }
            return false;
        }
        static Boolean isPlayingOrWatching(int time_firstObj, int time_current, int time_full = 0) 
        {
            if (time_current < time_firstObj | state != 2) 
            {
                return false;
            }
            if (time_firstObj < time_current & time_current < time_full & state == 2) 
            {
                return true;
            }
            return false;
        }
        static Boolean hasPassed(string active_mods, int time_current, int time_full) 
        {
            if (!active_mods.Contains("NF") & time_current > time_full & old_state == 2 & state == 7) 
            {
                return true;
            }
            return false;
        }
        static Boolean hasFCed(string active_mods, int time_current, int time_full, int hits_sliderBreaks, int hits_miss) 
        {
            if (hasPassed(active_mods, time_current, time_full) & hits_sliderBreaks == 0 & hits_miss == 0) 
            {
                return true;
            }
            return false;
        }
        static int getPropertyIndex(IEnumerable<string> enumerable, string property) {
            for (int i = 0; i != enumerable.Count(); i++) 
            {
                if (enumerable.ElementAt(i) == $"[{property}]") {
                    return i;
                }
            }
            throw new Exception("The enumerable doesn't contain a valid osu! beatmap");
        }
        static List<string> prepareBeatmapHeader(List<string> enumerable, string songFolder, string file) {
            string diffname = "";
            string nextLevel = getNextLevel(songFolder, file);
            for (int i = enumerable.Count-1; i >= 0; i--) {
                switch(enumerable[i].Split(":")[0]){
                    case "AudioFilename":
                        enumerable[i] = $"AudioFilename: level{getNextLevel(songFolder, file)}.mp3";
                        break;
                    case "Tags":
                        enumerable[i] = "Tags: Osu!Progress level="+nextLevel;
                        break;
                    case "BeatmapID":
                        enumerable[i] = "BeatmapID:0";
                        break;
                    case "BeatmapSetID":
                        enumerable[i] = "BeatmapSetID:-1";
                        break;
                    case "Title":
                        enumerable[i] = $"Title:Osu!Progress - Long Stream Practice Maps [{diffname}]";
                        break;
                    case "TitleUnicode":
                        enumerable[i] = $"TitleUnicode:Osu!Progress - Long Stream Practice Maps [{diffname}]";
                        break;
                    case "Version":
                        diffname = enumerable[i].Split(":")[1];
                        enumerable[i] = $"Version:Level {nextLevel}";
                        break;
                }
            }
            return enumerable;
        }
        static string getNextLevel(string songFolder, string file) 
        {
            string newMapPath = Path.Combine(songFolder, "Osu!Progress - "+file.Replace(".osu", ""));
            string[] allMaps = Directory.GetFiles(newMapPath, "*.osu");
            return (allMaps.Length+1).ToString();
        }
        static List<string> setTimings(List<string> section, int sectionStart) 
        {
            for (int line = 0; line != section.Count; line++)
            {
                var lineText = section[line].Split(",");
                if (lineText[3] == "12") 
                {
                    lineText[2] = $"{sectionStart + int.Parse(lineText[2])}";
                    lineText[5] = $"{sectionStart + int.Parse(lineText[5])}";
                }
                else
                {
                    lineText[2] = $"{sectionStart + int.Parse(lineText[2])}";
                }

                section[line] = "";
                for (int j = 0; j != lineText.Length; j++) 
                {   
                    section[line] += lineText[j];
                    if (j != lineText.Length-1) 
                    {
                        section[line] += ",";
                    }
                }
            }
            return section;
        }
        static void MakeMP3(float multiplier, int mapDuration, string songPath, string output) 
        {
            Console.WriteLine("Making MP3...");
            List<Mp3Frame> frames = new List<Mp3Frame>();
            for (float i = multiplier; i >= 1; i--) 
            {
                if (i == 0) 
                {
                    frames = TrimMp3(songPath, frames, TimeSpan.FromMinutes(0), TimeSpan.FromMilliseconds(getSongDuration(songPath)));
                    return;
                }
                if (i-1 < 1 & i != 0) 
                {
                    frames = TrimMp3(songPath, frames, TimeSpan.FromMinutes(0), TimeSpan.FromMilliseconds(mapDuration+3000));
                } 
                else
                {
                    frames = TrimMp3(songPath, frames, TimeSpan.FromMinutes(0), TimeSpan.FromMilliseconds(mapDuration));
                }
                multiplier--;
            }
            if (multiplier > 0) 
            {
                int songDuration = getSongDuration(songPath);
                // we want to always trim the song from the end to prevent notes from being misstimed when placed
                frames = TrimMp3(songPath, frames, TimeSpan.FromMilliseconds(songDuration*(1-multiplier)), TimeSpan.FromMilliseconds(songDuration));
                writeMP3(frames, output);
            }
        }
        static List<Mp3Frame> TrimMp3(string input, List<Mp3Frame> output, TimeSpan? begin, TimeSpan? end)
        {
            using (var reader = new Mp3FileReader(input))
            //using (var writer = File.Create(output))
            {           
                Mp3Frame frame;
                while ((frame = reader.ReadNextFrame()) != null)
                if (reader.CurrentTime >= begin || !begin.HasValue)
                {
                    if (reader.CurrentTime <= end || !end.HasValue) 
                    {
                        output.Add(frame);
                        //writer.Write(frame.RawData,0,frame.RawData.Length); 
                    }       
                    else 
                    {
                        break;
                    }
                }
            }
            return output;
        }
        static void writeMP3(List<Mp3Frame> frames, string output) 
        {
            using (var writer = File.Create(output)) 
            {
                for (int i = 0; i != frames.Count; i++) 
                {
                    writer.Write(frames[i].RawData,0,frames[i].RawData.Length);
                }
            }
        }
        static int getMP3FrameLength(string input) 
        {
            using (var writer = File.Create("frame.mp3"))
            using (var reader = new Mp3FileReader(input)) 
            {
                Mp3Frame frame = reader.ReadNextFrame();
                writer.Write(frame.RawData,0,frame.RawData.Length);
            }
            return getSongDuration("frame.mp3");
        }
        static int getSectionDuration(string input, int mapDuration, int startDuration = 0) {
            List<Mp3Frame> frames = new List<Mp3Frame>();

            frames = TrimMp3(input, frames, TimeSpan.FromMinutes(startDuration), TimeSpan.FromMilliseconds(mapDuration));
            writeMP3(frames, "section.mp3");
            return getSongDuration("section.mp3");
        }
        static int getSongDuration(string input) 
        {
            TimeSpan duration;
            using (var reader = new Mp3FileReader(input)) 
            {
                duration = reader.TotalTime;
            }
            return (int)duration.TotalMilliseconds;
        }
        static int getLastNotetime(List<string> map) {
            for (int j = map.Count-1; j >= 0; j--) 
            {
                if (!string.IsNullOrEmpty(map[j])) 
                {
                    if (map[j].Split(",")[3] == "5" | map[j].Split(",")[3] == "1")
                    {
                        return int.Parse(map[j].Split(",")[2]);
                    }
                }
            }
            throw new Exception("Expected an osu! map, got something else.");
        }
    }
}