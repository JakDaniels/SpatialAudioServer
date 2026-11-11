using System.Text.Json.Serialization;

namespace AudioMixerTest3
{
    public class ParticipantBaseModel
    {
        [JsonIgnore]
        public Guid Uuid { get; set; }

        [JsonIgnore]
        public string Name { get; set; } = string.Empty;

        [JsonIgnore]
        public bool IsConnected { get; set; }
        
        [JsonIgnore]
        public bool IsChanged { get; set; }

        /// <summary>
        /// Data Channel Interface for WebRTC Voice
        /// The server listens for and sends data on the “SLData” data channel. Clients must create the
        /// “SLData” data channel before negotiation, just as clients create the audio channels.
        /// Communication over the data channels consists of json as strings (as opposed to binary.)
        /// All values are not required for an update. It’s expected that values should only be sent if they’re
        /// changed, to reduce bandwidth.
        /// Client -> Mixer
        /// Example:
        /// {
        ///     “j”: { “p”: true },
        ///     “sp”: { “x”: 37000, “y” : 2400, “z” : 37201 },
        ///     “lh” : { “x”: 100, “y” : 50, “z”: 1, “w”: 100 },
        ///     “m” : {
        ///     “98d0084e-a6eb - 45ce - b847 - 4da34ea823a8”:true,
        ///     “95c8ef1a - edcb - 467c - 88fe - ea503e5b974e”: false
        ///     },
        ///     “ug”: {
        ///     “98d0084e-a6eb - 45ce - b847 - 4da34ea823a8”:27,
        ///     “95c8ef1a - edcb - 467c - 88fe - ea503e5b974e”: 125
        ///     },
        /// }
        /// </summary>

        /// <summary>
        /// “j” : join (an object) - indicating that this client is joining a session. The sole member of the
        /// object is an option “p” which indicates this is a primary connection. Primary connections are the
        /// ones for which audio levels are streamed back to the client. The default for “p” is false.
        /// </summary>
        public class Join
        {
            public bool Primary { get; set; } = false;
        }

        [JsonPropertyName("j")]
        public Join? J { get; set; }

        /// <summary>
        /// “l”: leave (a boolean, always true) - indicates that this client is leaving a session.
        /// </summary>
        
        [JsonPropertyName("l")]
        public bool Leave { get; set; } = true;

        /// <summary>
        /// “lp” : listener position (an object) containing integer “x,” “y,” and “z,” coordinates for the sender.
        /// These coordinates are world coordinates (not region-relative) in centimeters. (only for spatial
        /// voice)
        /// </summary>
        public class ListenerPosition
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }
        }

        /// <summary>
        /// “lh” : listener heading (an object) containing integer “x,” “y,” “z,” “w” quaternion values (multiplied
        /// by 100) (only for spatial voice)
        /// </summary>
        public class ListenerHeading
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }
            public int W { get; set; }
        }

        /// <summary>
        /// “sp” : sender position (an object) containing integer “x,” “y,” and “z,” coordinates for the sender.
        /// These coordinates are world coordinates (not region-relative) in centimeters. (only for spatial
        /// voice)
        /// </summary>
        public class SenderPosition
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }
        }

        /// <summary>
        /// “sh” : sender heading (an object) containing integer “x,” “y,” “z,” “w” quaternion values (multiplied
        /// by 100) (only for spatial voice)
        /// </summary>
        public class SenderHeading
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }
            public int W { get; set; }
        }

        /// <summary>
        /// “m”: agents to mute or unmute. An object associating agent ids with true/false (mute/unmute)
        /// </summary>
        [JsonPropertyName("m")]
        public Dictionary<Guid, bool> Mutelist { get; set; } = new Dictionary<Guid, bool>();

        /// <summary>
        /// “ug”: set gain on agents. An object associating agent ids with integer gains (gain * 200)
        /// </summary>
        [JsonPropertyName("ug")]
        public Dictionary<Guid, int> Gainlist { get; set; } = new Dictionary<Guid, int>();

        /// <summary>
        /// “lp” : listener position (an object) containing integer “x,” “y,” and “z,” coordinates for the sender.
        /// These coordinates are world coordinates (not region-relative) in centimeters. (only for spatial
        /// voice)
        /// </summary>
        [JsonPropertyName("lp")]
        public ListenerPosition? Lp { get; set; }

        /// <summary>
        /// “lh” : listener heading (an object) containing integer “x,” “y,” “z,” “w” quaternion values (multiplied
        /// by 100) (only for spatial voice)
        /// </summary>
        [JsonPropertyName("lh")]
        public ListenerHeading? Lh { get; set; }

        /// <summary>
        /// “sp” : sender position (an object) containing integer “x,” “y,” and “z,” coordinates for the sender.
        /// These coordinates are world coordinates (not region-relative) in centimeters. (only for spatial
        /// voice)
        /// </summary>
        [JsonPropertyName("sp")]
        public SenderPosition? Sp { get; set; }

        /// <summary>
        /// “sh” : sender heading (an object) containing integer “x,” “y,” “z,” “w” quaternion values (multiplied
        /// by 100) (only for spatial voice)
        /// </summary>
        [JsonPropertyName("sh")]
        public SenderHeading? Sh { get; set; }


    }
}