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
namespace EtheirysProximityVoiceChat.WebRTC
{
    // Kept in the WebRTC namespace for backwards-compat with existing usings.
    // In v2 these structs no longer carry SDP/ICE — just presence. The server
    // also still emits a `turnConfig` field on "open" messages for v1 clients;
    // we silently ignore it on the wire.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public struct SignalMessage
    {
        public struct SignalPayload
        {
            public struct Connection
            {
                public string socketId;
                public string peerId;
                public string peerType;
                public ushort audioState;
                // Server-stamped: true iff this peer's premium token resolved to
                // a Discord account holding the configured supporter role.
                public bool isPremium;

                // Profile data. Server blanks these for non-premium peers, so
                // a populated value here is itself evidence that isPremium=true.
                public string pronoun;
                public string biography;
                public string status;
                public string color;
                public string twitchLink;
            }

            public string action;
            public Connection[] connections;
            public bool? bePolite;
        }

        public string from;
        public string target;
        public SignalPayload payload;
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
}
