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
namespace EtheirysProximityVoiceChat.WebRTC;

public enum SignalingChannelError
{
    Unknown = 0,
    IncorrectPrivateRoomPassword = 1,
    NonexistentPrivateRoom = 2,
    KickedFromChannel = 3,
    /// <summary>v4: room is at the 24-peer cap.</summary>
    PrivateRoomFull = 4,
    /// <summary>v4: this peerId is banned from the room.</summary>
    BannedFromPrivateRoom = 5,
    /// <summary>v4: room display name was rejected by the server blocklist or length cap.</summary>
    InvalidPrivateRoomName = 6,
    /// <summary>v4: caller already owns one room and tried to create another.</summary>
    AlreadyOwnAnotherRoom = 7,
    UnsupportedOperatingSystem = 10,
}