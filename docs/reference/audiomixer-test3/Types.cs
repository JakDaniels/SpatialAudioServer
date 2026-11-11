namespace AudioMixerTest3
{
    public class SourceType
    {
        /// <summary>
        /// Audio source from file
        /// </summary>
        public const int File = 0;
        /// <summary>
        /// Audio source from test tone
        /// </summary>
        public const int TestTone = 1;
        /// <summary>
        /// Audio source from RTP stream
        /// </summary>
        public const int Rtp = 2;
    }

    public class SinkType
    {
        /// <summary>
        /// Audio sink to Null
        /// </summary>
        /// <remarks>Null sink is a special sink that discards all data written to it.</remarks>
        /// <seealso href="https://gstreamer.freedesktop.org/documentation/null/null.html?gi-language=c"/>
        /// <seealso href="https://gstreamer.freedesktop.org/documentation/null/null.html?gi-language=c#GstElement--sink"/>
        public const int Null = 0;
        
        /// <summary>
        /// Audio sink to file
        /// </summary>
        public const int File = 1;

        /// <summary>
        /// Audio sink to PC speakers
        /// </summary>
        public const int AudioOut = 2;

        /// <summary>
        /// Audio sink to RTP stream
        /// </summary>
        public const int Rtp = 3;
    }

}