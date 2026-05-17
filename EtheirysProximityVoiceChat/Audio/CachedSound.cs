/*
 * Copyright (c) 2026 Noah Dolph
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
 *
 * See the GNU Affero General Public License for more details.
 */
using NAudio.Wave;
using System.Collections.Generic;
using System.Linq;

namespace EtheirysProximityVoiceChat.Audio;

// https://markheath.net/post/fire-and-forget-audio-playback-with
public class CachedSound
{
    public float[] AudioData { get; private set; }
    public WaveFormat WaveFormat { get; private set; }
    
    public CachedSound(string audioFileName)
    {
        using (var reader = new AudioFileReader(audioFileName))
        {
            WaveFormat = reader.WaveFormat;
            var wholeFile = new List<float>((int)(reader.Length / 4));
            var readBuffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
            int samplesRead;
            while ((samplesRead = reader.Read(readBuffer, 0, readBuffer.Length)) > 0)
            {
                wholeFile.AddRange(readBuffer.Take(samplesRead));
            }
            AudioData = wholeFile.ToArray();
        }
    }
}
