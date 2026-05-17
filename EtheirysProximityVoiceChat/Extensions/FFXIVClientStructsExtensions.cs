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
using FFXIVClientStructs.FFXIV.Client.Game;

namespace EtheirysProximityVoiceChat.Extensions;

public static class FFXIVClientStructsExtensions
{
    public static bool IsValid(this HouseId houseId)
    {
        return houseId != ulong.MaxValue;
    }
}
