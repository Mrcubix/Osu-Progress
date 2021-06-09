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
using System.Text.RegularExpressions;

namespace Osu_Progress
{
    class Program
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AllocConsole();
        private static readonly ManualResetEvent ExitEvent = new ManualResetEvent(false);
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

                client.ReconnectionHappened.Subscribe(msg => {
                    Console.WriteLine("Info: Connected to gosumemory.");
                });
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
            string mapFileName = path["file"].ToString();
            string bg = path["bg"].ToString();
            string mapFolder = Path.Combine(songFolder, path["folder"].ToString());
            string songFileName = path["audio"].ToString();

            if (hasPassed(active_mods, time_current, time_full)) 
            {
                Console.WriteLine("Map has been Passed");
                if (!bool.Parse(deserializedSettings["require_FC"].ToString())) 
                {
                    MakeMap(mapFolder, mapFileName, songFileName, bg, Multiplier);
                }
            }
            if (hasFCed(active_mods, time_current, time_full, hits_sliderBreaks, hits_miss)) 
            {
                Console.WriteLine("Map has been FCed");
                if (bool.Parse(deserializedSettings["require_FC"].ToString())) 
                {   
                    MakeMap(mapFolder, mapFileName, songFileName, bg, Multiplier);
                }
            }
            old_state = state;
        }
        static void MakeMap(string mapFolder, string map_fileName, string song_fileName, string bg_fileName, float user_length_multiplier) 
        {   
            string mapPath = Path.Combine(mapFolder, map_fileName);
            string songPath = Path.Combine(mapFolder, song_fileName);

            int level = 0;
            int next_level = 1;
            string new_map_folder;
            Dictionary<string, string> original_map_info = new Dictionary<string, string>();
            if (isOsuProgressMap(mapFolder)) 
            {
                new_map_folder = mapFolder;
                original_map_info = getOriginalMapInfo(mapFolder);
                mapFolder = original_map_info["original_Map_folder"];
                songPath = Path.Combine(mapFolder, original_map_info["original_Song_fileName"]);
                mapPath = Path.Combine(mapFolder, original_map_info["original_Map_fileName"]);
                bg_fileName = original_map_info["original_BG_fileName"];
                level = int.Parse(original_map_info["level"]);
                if (level == getCurrentMapLevel(mapPath)) {
                    next_level = level + 1;
                }
            } 
            else 
            {
                new_map_folder = Path.Combine(songFolder, "Osu!Progress - "+map_fileName.Replace(".osu", ""));
                if (!Directory.Exists(new_map_folder)) 
                {
                    Directory.CreateDirectory(new_map_folder);
                } else {
                    if (isOsuProgressMap(new_map_folder)) 
                    {
                        Console.WriteLine("Info: An Osu!Progress variant of this map already exist.");
                        return;
                    }
                }
                original_map_info = WriteMapInfo(mapFolder, map_fileName, song_fileName, bg_fileName, new_map_folder);
            }

            Console.WriteLine("Making map...");
            // Copy original background to new map
            File.Copy(Path.Combine(mapFolder, bg_fileName), Path.Combine(new_map_folder,$"{bg_fileName}"),true);
            // Read all lines of the map and store them line by line in a string array
            List<string> map = File.ReadAllLines(mapPath).ToList();
            // Store the map header separately in another array (header from 0 to hitObject property)
            List<string> header = map.GetRange(0, getPropertyIndex(map, "HitObjects")+1);
            // Edit Different variable in this header in preperation for the new map
            header = prepareBeatmapHeader(header, songFolder, map_fileName, next_level);

            int mapDuration = getLastNotetime(map);
            float length_modifier = user_length_multiplier - 1;
            float multiplier = 1 + (next_level * length_modifier);
            string new_song_path = Path.Combine(new_map_folder, $"level{next_level}.mp3");

            MakeMP3(multiplier, mapDuration, songPath, new_song_path);
            int newMP3Duration = getSongDuration(new_song_path); 

            // TODO:
            // - find a way to make this process faster (can take several minutes)
            List<string> newMap = new List<string>();
            int sectionStart = 0;
            for (float i = multiplier; i >= 1; i--) 
            {
                Console.WriteLine(sectionStart);
                multiplier--;
                if (i == 0) 
                {
                    int sectionDuration = getSectionDuration(songPath, getSongDuration(songPath));
                    List<string> section = map.GetRange(getPropertyIndex(map, "HitObjects") + 1, (map.Count - 1) - getPropertyIndex(map, "HitObjects"));
                    newMap.AddRange(setTimingsBeforeEnd(section, sectionStart));
                    sectionStart += sectionDuration;
                    break;
                }
                if (i-1 < 1 & i != 0) 
                {
                    int sectionDuration = 0;
                    List<string> section = map.GetRange(getPropertyIndex(map, "HitObjects") + 1, (map.Count - 1) - getPropertyIndex(map, "HitObjects"));
                    section = removeEndSliderSpinner(section);
                    sectionDuration = getSectionDuration(songPath, getLastNotetime(section)+3000);
                    newMap.AddRange(setTimingsBeforeEnd(section, sectionStart));
                    sectionStart += sectionDuration;
                    break;
                }
                else
                {
                    int sectionDuration = 0;
                    List<string> section = map.GetRange(getPropertyIndex(map, "HitObjects") + 1, (map.Count - 1) - getPropertyIndex(map, "HitObjects")); 
                    section = removeEndSliderSpinner(section);
                    sectionDuration = getSectionDuration(songPath, getLastNotetime(section));
                    newMap.AddRange(setTimingsBeforeEnd(section, sectionStart));
                    sectionStart += sectionDuration;
                    break;      
                }
            }
            Console.WriteLine(sectionStart);
            if (multiplier > 0) {
                List<string> section = map.GetRange(getPropertyIndex(map, "HitObjects") + 1, (map.Count - 1) - getPropertyIndex(map, "HitObjects"));
                section = setTimingEnding(section, songPath, newMP3Duration, multiplier);
                newMap.AddRange(section);
            }
            using (TextWriter tw = new StreamWriter(Path.Combine(new_map_folder, $"Osu!Progress - {map_fileName.Split(".osu")[0]} - level {next_level}.osu"))) 
            {
                foreach (String line in header)
                    tw.WriteLine(line);
                foreach (String line in newMap)
                    tw.WriteLine(line);
            }

            original_map_info["level"] = next_level.ToString();
            var serialized_map_info = JsonSerializer.Serialize(original_map_info);
            File.WriteAllText(Path.Combine(new_map_folder, "original_map_info.json"), serialized_map_info);

            Console.WriteLine("Done");
        }
        static Boolean isOsuProgressMap(string map_folder) {
            string original_map_info_path = Path.Combine(map_folder, "original_map_info.json");
            if (File.Exists(original_map_info_path)) 
            {
                var deserialized_content = getOriginalMapInfo(map_folder);
                if (deserialized_content["generated_by"].ToString() == "Osu!Progress") {
                    return true;
                }
            }
            return false;
        }
        static Dictionary<string, string> WriteMapInfo(string map_folder, string map_fileName, string song_fileName, string bg_fileName, string new_map_folder) {
            Dictionary<string, string> map_info = new Dictionary<string, string>();
            map_info.Add("generated_by", "Osu!Progress");
            map_info.Add("original_Map_folder", map_folder);
            map_info.Add("original_Map_fileName", map_fileName);
            map_info.Add("original_Song_fileName", song_fileName);
            map_info.Add("original_BG_fileName", bg_fileName);
            map_info.Add("level", "1");
            var serialized_map_info = JsonSerializer.Serialize(map_info);
            File.WriteAllText(Path.Combine(new_map_folder, "original_map_info.json"), serialized_map_info);
            return map_info;
        }
        static Dictionary<string, string> getOriginalMapInfo(string map_folder) 
        {
            Console.WriteLine(map_folder);
            string original_map_info_path = Path.Combine(map_folder, "original_map_info.json");
            string serialized_content;
            using (var stream = new StreamReader(File.OpenRead(original_map_info_path))) 
            {
                serialized_content = stream.ReadToEnd();
            }
            var deserialized_content = JsonSerializer.Deserialize<Dictionary<string, string>>(serialized_content);
            return deserialized_content;
        }
        static int? getCurrentMapLevel(string map_path) 
        {
            string[] map = File.ReadAllLines(map_path);
            foreach (var line in map) 
            {
                if (line.Contains("Version:")) {
                    return int.Parse(Regex.Match(line, @"[0-9]+").Groups[0].ToString());
                }
            }
            return null;
        }
        static List<string> removeEndSliderSpinner(List<string> section) 
        {
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
                        return section;
                    }
                }
            }
            return section;
        }
        static Dictionary<string, object> toTrueDictionary(string stringtoconvert) 
        {
            Dictionary<string, object> dictionary = JsonSerializer.Deserialize<Dictionary<string, object>>(stringtoconvert);
            return dictionary;
        }
        static Boolean hasPassed(string active_mods, int time_current, int time_full) 
        {
            if ((!active_mods.Contains("NF") | !active_mods.Contains("RX") | !active_mods.Contains("AP") | !active_mods.Contains("SO")) & time_current > time_full & old_state == 2 & state == 7) 
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
        static List<string> prepareBeatmapHeader(List<string> enumerable, string songFolder, string file, int nextLevel) {
            string diffname = "";
            for (int i = enumerable.Count-1; i >= 0; i--) {
                switch(enumerable[i].Split(":")[0]){
                    case "AudioFilename":
                        enumerable[i] = $"AudioFilename: level{nextLevel}.mp3";
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
        static List<string> setTimingsBeforeEnd(List<string> section, int sectionStart) 
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
        static List<string> setTimingEnding(List<string> section, string songPath, int newMP3Duration, float multiplier) {
            int sectionDuration = (int)(getSectionDuration(songPath, getSongDuration(songPath)) * multiplier);
            for (int j = section.Count-1; j >= 0; j--) 
            {
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
            using (var writer = new FileStream(output, FileMode.Create, FileAccess.Write)) 
            {
                for (int i = 0; i != frames.Count; i++) 
                {
                    writer.Write(frames[i].RawData,0,frames[i].RawData.Length);
                }
            }
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