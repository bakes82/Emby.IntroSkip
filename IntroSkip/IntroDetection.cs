﻿/*
 * Plugin intro detection code translated from it's original author:
 * https://gist.githubusercontent.com/puzzledsam/c0731702a9eab244afacbcb777c9f5e9/raw/1fd81d1101eebc08d442acfd88742b5e5635f1ab/introDetection.py
 * Intro detection algorithm is derived from VictorBitca/matcher, which was originally written in Go. https://github.com/VictorBitca/matcher *
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

// ReSharper disable ComplexConditionExpression
// ReSharper disable once TooManyDependencies

namespace IntroSkip
{
    public class IntroDetection : TitleSequenceDto, IServerEntryPoint
    {
        private IJsonSerializer JsonSerializer     { get; }
        private IFileSystem FileSystem             { get; }
        private IFfmpegManager FfmpegManager       { get; }
        private static ILogger Logger              { get; set; }
        public static IntroDetection Instance      { get; private set; }
        private IApplicationPaths ApplicationPaths { get; }
       
        public IntroDetection(IJsonSerializer json, IFileSystem file, ILogManager logMan, IFfmpegManager f, IApplicationPaths applicationPaths)
        {
            JsonSerializer   = json;
            FileSystem       = file;
            ApplicationPaths = applicationPaths;
            FfmpegManager    = f;
            Logger           = logMan.GetLogger(Plugin.Instance.Name);
            Instance         = this;
        }

        
        // Keep integer in specified range
        private static int clip(int val, int min, int max)
        {
            if (val < min)
            {
                return min;
            }
            else if (val > max)
            {
                return max;
            }
            else
            {
                return val;
            }
        }

        // Calculate Hamming distance between to integers (bit difference)
        private static uint getHammingDistance(uint n1, uint n2)
        {
            var x = n1 ^ n2;
            uint setBits = 0;
            while (x > 0)
            {
                setBits += x & 1;
                x >>= 1;
            }

            return setBits;
        }

        // Calculate the similarity of two fingerprints
        private static double compareFingerprints(List<uint> f1, List<uint> f2)
        {
            double dist = 0.0;
            if (f1.Count != f2.Count)
            {
                return 0;
            }

            foreach (var i in Enumerable.Range(0, f1.Count))
            {
                var hammingDistance = getHammingDistance(f1[i], f2[i]);
                dist = dist + hammingDistance;
            }

            var score = 1 - dist / (f1.Count * 32);
            return score;
        }

        // Slide fingerprints to find best offset
        private static int getBestOffset(List<uint> f1, List<uint> f2)
        {
            var length     = f1.Count;
            var iterations = length + 1;
            var diff       = length / 2 - 1;
            var a          = length / 2;
            var b          = length - 1;
            var x          = 0;
            var y          = length / 2 - 1;

            var output = new List<double>();

            foreach (var i in Enumerable.Range(0, iterations))
            {
                var upper = Math.Abs(a - b);
                try
                {
                    output.Add(compareFingerprints(f1.GetRange(a, upper), f2.GetRange(x, upper)));
                   
                    a = clip(a - 1, 0, length - 1);
                    if (diff < 0)
                    {
                        b = clip(b - 1, 0, length - 1);
                        x = clip(x + 1, 0, length - 1);
                        y = clip(y, 0, length - 1);
                    }
                    else
                    {
                        b = clip(b, 0, length - 1);
                        x = clip(x, 0, length - 1);
                        y = clip(y + 1, 0, length - 1);
                    }

                    diff = diff - 1;
                }
                catch (Exception ex)
                {
                    Logger.Info("Get Best Offset Error: " + ex.Message);
                    throw new InvalidIntroDetectionException(ex.Message);
                }
            }

            var index = output.IndexOf(output.Max());

            return (iterations - 1) / 2 - index;
        }

        // Align the fingerprints according to the calculated offset
        private static Tuple<List<uint>, List<uint>> getAlignedFingerprints(int offset, List<uint> f1, List<uint> f2)
        {
            List<uint> offsetCorrectedF2 = new List<uint>();
            List<uint> offsetCorrectedF1 = new List<uint>();
            if (offset >= 0)
            {
                //offset = offset * -1;
                offsetCorrectedF1.AddRange(f1.GetRange(offset, f1.Count - offset));
                offsetCorrectedF2.AddRange(f2.GetRange(0, f2.Count - offset));

            }
            else
            {
                offset = offset * -1;
                offsetCorrectedF1.AddRange(f1.GetRange(0, f1.Count - Math.Abs(offset)));
                offsetCorrectedF2.AddRange(f2.GetRange(offset, f2.Count - Math.Abs(offset)));

            }

            return Tuple.Create(offsetCorrectedF1, offsetCorrectedF2);
        }

        // Find the intro region based on Hamming distances
        private static Tuple<int, int> findContiguousRegion(List<uint> arr, int upperLimit)
        {
            var start = -1;
            var end = -1;
            foreach (var i in Enumerable.Range(0, arr.Count()))
            {
                // Stop the execution after we've been far enough past the found intro region
                if (start != -1 && i - end >= 100) {
                    break;
                }
                if (arr[i] < upperLimit && nextOnesAreAlsoSmall(arr, i, upperLimit))
                {
                    if (start == -1)
                    {
                        start = i;
                    }

                    end = i;
                }
            }

            return Tuple.Create(start, end);
        }

        // Look at next elements in the array and determine if they also fall below the upper limit
        private static bool nextOnesAreAlsoSmall(List<uint> arr, int index, int upperLimit)
        {
            if (index + 3 < arr.Count())
            {
                var v1 = arr[index + 1];
                var v2 = arr[index + 2];
                var v3 = arr[index + 3];
                var average = (v1 + v2 + v3) / 3;
                if (average < upperLimit)
                {
                    return true;
                }

                return false;
            }

            return false;
        }

        private void ExtractPCMAudio(string input, string audioOut)
        {
            var ffmpegPath = FfmpegManager.FfmpegConfiguration.EncoderPath;
            Logger.Info(ffmpegPath);
            var procStartInfo =
                new ProcessStartInfo(ffmpegPath,
                    $"-t 00:10:00 -i \"{input}\" -ac 1 -acodec pcm_s16le -ar 16000 \"{audioOut}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                };

            var process = new Process {StartInfo = procStartInfo};

            process.Start();

            string processOutput = null;
            
            while ((processOutput = process.StandardError.ReadLine()) != null)
            {
                //Logger.Info(processOutput);
            }
        }

        private string FingerPrintAudio(string inputFileName)
        {
            // Using 300 second length to get a more accurate fingerprint, but it's not required
            
            var configPath = ApplicationPaths.PluginConfigurationsPath + FileSystem.DirectorySeparatorChar;
            
            var fpcalc = configPath + "fpcalc" + (OperatingSystem.IsWindows() ? ".exe" : "");
            var procStartInfo =
                new ProcessStartInfo(fpcalc, $"{inputFileName} -raw -length 600 -json")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                };

            var process = new Process {StartInfo = procStartInfo};
            process.Start();
            string processOutput = null;
            var json = "";

            while ((processOutput = process.StandardOutput.ReadLine()) != null)
            {
                json += (processOutput);
                //Logger.Info(processOutput);
            }
           
            return json;
        }

        private string audio1_save_path  = "";
        private string audio2_save_path  = "";
        
        private static string EpisodeComparable { get; set; }
        private static string EpisodeToCompare  { get; set; }
        
        public List<EpisodeTitleSequence> SearchAudioFingerPrint(BaseItem episode1Input, BaseItem episode2Input)
        {
            var encodingPath = ApplicationPaths.PluginConfigurationsPath + FileSystem.DirectorySeparatorChar + "IntroEncoding" + FileSystem.DirectorySeparatorChar;
            Logger.Info("Starting episode intro detection process.");
            Logger.Info($" {episode1Input.Parent.Parent.Name} - Season: {episode1Input.Parent.IndexNumber} - Episode Comparable: {episode1Input.IndexNumber}");
            Logger.Info($" {episode2Input.Parent.Parent.Name} - Season: {episode2Input.Parent.IndexNumber} - Episode To Compare: {episode2Input.IndexNumber}");

            //Create the the current episode input key. Season.InternalId + episode.InternalId
            var episode1InputKey = $"{episode1Input.Parent.InternalId}{episode1Input.InternalId}";
            var episode2InputKey = $"{episode2Input.Parent.InternalId}{episode2Input.InternalId}";
            

            if (EpisodeComparable is null || episode1InputKey != EpisodeComparable)
            {
                EpisodeComparable = episode1InputKey;

                audio1_save_path = $"{encodingPath}{EpisodeComparable}.wav";

                //Check and see if we have encoded this episode before
                if (!FileSystem.FileExists(audio1_save_path))
                {
                    Logger.Info($"Beginning Audio Extraction for Comparable Episode: {episode1Input.Path}");
                    ExtractPCMAudio(episode1Input.Path, audio1_save_path);
                }
            }


            if (EpisodeToCompare is null || episode2InputKey != EpisodeToCompare)
            {
                EpisodeToCompare = episode2InputKey;

                audio2_save_path = $"{encodingPath}{EpisodeToCompare}.wav";

                if (!FileSystem.FileExists(audio2_save_path))
                {
                    Logger.Info($"Beginning Audio Extraction for Comparing Episode: {episode2Input.Path}");
                    ExtractPCMAudio(episode2Input.Path, audio2_save_path);
                }
            }


            Logger.Info("Audio Extraction Ready.");

            var introDto =  AnalyzeAudio();

            introDto[0].InternalId       = episode1Input.InternalId;
            introDto[0].IndexNumber      = episode1Input.IndexNumber;
            introDto[1].InternalId       = episode2Input.InternalId;
            introDto[1].IndexNumber      = episode2Input.IndexNumber;
            
            Logger.Info($"\n{episode1Input.Parent.Parent.Name} - S: {episode1Input.Parent.IndexNumber} - E: {episode1Input.IndexNumber} \nStarts: {introDto[0].IntroStart} \nEnd: {introDto[0].IntroEnd}\n\n");
            Logger.Info($"\n{episode2Input.Parent.Parent.Name} - S: {episode2Input.Parent.IndexNumber} - E: {episode2Input.IndexNumber} \nStarts: {introDto[1].IntroStart} \nEnd: {introDto[1].IntroEnd}\n\n");
            
            return introDto;
        }

        private List<EpisodeTitleSequence> AnalyzeAudio()
        {
            Logger.Info("Analyzing Audio...");
            Logger.Info(audio1_save_path);
            Logger.Info(audio2_save_path);

            var audio1Json = FingerPrintAudio(audio1_save_path);
            var fingerPrintDataEpisode1 = JsonSerializer.DeserializeFromString<IntroAudioFingerprint>(audio1Json);
            if (fingerPrintDataEpisode1 is null)
            {
                Logger.Info("Trying new episode");
                throw new InvalidIntroDetectionException($"Episode detection failed to find a fingerprint. {audio1_save_path}");
            }
            Logger.Info($"Fingerprint 1 Duration {fingerPrintDataEpisode1.duration}");
            
            var audio2Json = FingerPrintAudio(audio2_save_path);
            var fingerPrintDataEpisode2 = JsonSerializer.DeserializeFromString<IntroAudioFingerprint>(audio2Json);
            if (fingerPrintDataEpisode2 is null)
            {
                Logger.Info("Trying new episode");
                throw new InvalidIntroDetectionException($"Episode detection failed to find a fingerprint. {audio2_save_path}");
            }

            Logger.Info($"Fingerprint 2 Duration {fingerPrintDataEpisode2.duration}");

            var fingerprint1 = fingerPrintDataEpisode1.fingerprint;
            var fingerprint2 = fingerPrintDataEpisode2.fingerprint;
            
            Logger.Info("Analyzing Finger Prints..");

            // We'll cut off a bit of the end if the fingerprints have an odd numbered length
            if (fingerprint1.Count % 2 != 0)
            {
                fingerprint1 = fingerprint1.GetRange(0, fingerprint1.Count() - 1);  
                fingerprint2 = fingerprint2.GetRange(0, fingerprint2.Count() - 1);  
            }

            Logger.Info("Analyzing Offsets..");
            int offset = getBestOffset(fingerprint1, fingerprint2);

            Logger.Info($"The calculated fingerprint offset is {offset}");

            var _tup_1 = getAlignedFingerprints(offset, fingerprint1, fingerprint2);
            var f1     = _tup_1.Item1;
            var f2     = _tup_1.Item2;


            //Logger.Info("Calculating Hamming Distances.");
            var hammingDistances = Enumerable.Range(0, (f1.Count < f2.Count ? f1.Count : f2.Count)).Select(i => getHammingDistance(f1[i], f2[i])).ToList();
            Logger.Info("Calculate Hamming Distances Done.");
            
            var _tup_2 = findContiguousRegion(hammingDistances, 8);
            var start  = _tup_2.Item1;
            var end    = _tup_2.Item2;

            double secondsPerSample = 600.0 / fingerprint1.Count;

            var offsetInSeconds   = offset * secondsPerSample;
            var commonRegionStart = start * secondsPerSample;
            var commonRegionEnd   = (end * secondsPerSample);

            var firstFileRegionStart  = 0.0;
            var firstFileRegionEnd    = 0.0;
            var secondFileRegionStart = 0.0;
            var secondFileRegionEnd   = 0.0;

            if (offset >= 0)
            {
                firstFileRegionStart  = commonRegionStart + offsetInSeconds;
                firstFileRegionEnd    = commonRegionEnd + offsetInSeconds;
                secondFileRegionStart = commonRegionStart;
                secondFileRegionEnd   = commonRegionEnd;
            }
            else
            {
                firstFileRegionStart  = commonRegionStart;
                firstFileRegionEnd    = commonRegionEnd;
                secondFileRegionStart = commonRegionStart - offsetInSeconds;
                secondFileRegionEnd   = commonRegionEnd - offsetInSeconds;
            }

            // Check for impossible situation, or if the common region is deemed too short to be considered an intro
            if (start < 0 || end < 0)
            {
                firstFileRegionStart = 0.0;
                firstFileRegionEnd = 0.0;
                secondFileRegionStart = 0.0;
                secondFileRegionEnd = 0.0;
                throw new InvalidIntroDetectionException("Episode detection failed to find a reasonable intro start and end time.");
            }
            if (commonRegionEnd - commonRegionStart < 5.0)
            {
                // -1 means intro does not exists
                firstFileRegionStart  = -1.0;
                firstFileRegionEnd    = -1.0;
                secondFileRegionStart = -1.0;
                secondFileRegionEnd   = -1.0;
                throw new InvalidIntroDetectionException("Episode common region is deemed too short to be considered an intro.");
                
            } 
            else if (start == 0 && end == 0)
            {
                throw new InvalidIntroDetectionException("Episode common region are both 00:00:00.");
            }
            
            
            Logger.Info("Audio Analysis Complete.");
            
            
            //Task.Run(() => IntroServerEntryPoint.Instance.AudioFileCleanUp(audio2_save_path)).ConfigureAwait(false);
            
            return new List<EpisodeTitleSequence>()
            {
                new EpisodeTitleSequence()
                {
                    HasIntro   = true,
                    IntroStart = TimeSpan.FromSeconds(Math.Round(firstFileRegionStart)),
                    IntroEnd   = TimeSpan.FromSeconds(Math.Round(firstFileRegionEnd))
                },
                new EpisodeTitleSequence()
                {
                    HasIntro   = true,
                    IntroStart = TimeSpan.FromSeconds(Math.Round(secondFileRegionStart)),
                    IntroEnd   = TimeSpan.FromSeconds(Math.Round(secondFileRegionEnd))
                }
            };

            
        }
        
        //public List<IntroDto> CompareAudioFingerPrint(List<uint> fingerPrint, BaseItem episodeToCompare)
        //{
        //    ExtractPCMAudio(episodeToCompare.Path, $"{episodeToCompare.InternalId.ToString()}.wav");
        //    return null;
        //} 

        

        public void Dispose()
        {
            throw new NotImplementedException();
        }

        public void Run()
        {
           
            if (!FileSystem.DirectoryExists(ApplicationPaths.PluginConfigurationsPath + FileSystem.DirectorySeparatorChar + "IntroEncoding"))
            {
                FileSystem.CreateDirectory( ApplicationPaths.PluginConfigurationsPath + FileSystem.DirectorySeparatorChar + "IntroEncoding" );
            }
        }


    }
}
