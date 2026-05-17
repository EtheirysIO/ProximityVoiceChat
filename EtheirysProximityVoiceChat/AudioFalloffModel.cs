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
using System;

namespace EtheirysProximityVoiceChat;

[Serializable]
public class AudioFalloffModel
{
    public enum FalloffType
    {
        None = 0,
        InverseDistance = 1,
        ExponentialDistance = 2,
        LinearDistance = 3,
    }

    public FalloffType Type { get; set; } = FalloffType.InverseDistance;
    public float MinimumDistance { get; set; } = 5.0f;
    public float MaximumDistance { get; set; } = 20.0f;
    public float FalloffFactor { get; set; } = 1.5f;
}
