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
using System;

namespace EtheirysProximityVoiceChat.Audio;

//https://markheath.net/post/fire-and-forget-audio-playback-with 
public class CachedSoundSampleProvider(CachedSound cachedSound) : ISampleProvider
{
    private readonly CachedSound cachedSound = cachedSound;
    private long position;

    public int Read(float[] buffer, int offset, int count)
    {
        var availableSamples = cachedSound.AudioData.Length - position;
        var samplesToCopy = Math.Min(availableSamples, count);
        Array.Copy(cachedSound.AudioData, position, buffer, offset, samplesToCopy);
        position += samplesToCopy;
        return (int)samplesToCopy;
    }

    public WaveFormat WaveFormat => cachedSound.WaveFormat;
}
