using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Media;

/// <summary>
/// The boundary between the room domain and the GStreamer layer. <see cref="RoomManager"/> calls it after a change
/// is committed, outside its lock. The Phase 2 implementation builds and tears down pipelines and mix chains;
/// until then <see cref="NullRoomMedia"/> does nothing, and tests use it to run without GStreamer.
/// </summary>
public interface IRoomMedia
{
    void RoomCreated(RoomSnapshot room);

    void RoomDeleted(Guid roomId);

    void ParticipantAdded(ParticipantSnapshot participant);

    void ParticipantRemoved(Guid roomId, Guid participantId);

    /// <summary>Mute, gain, position or orientation changed.</summary>
    void ParticipantUpdated(ParticipantSnapshot participant);
}

public sealed class NullRoomMedia : IRoomMedia
{
    public void RoomCreated(RoomSnapshot room)
    {
    }

    public void RoomDeleted(Guid roomId)
    {
    }

    public void ParticipantAdded(ParticipantSnapshot participant)
    {
    }

    public void ParticipantRemoved(Guid roomId, Guid participantId)
    {
    }

    public void ParticipantUpdated(ParticipantSnapshot participant)
    {
    }
}
