// Ignore Spelling: Mongo Contrib

using MongoDB.Driver.Core.Connections;

namespace PrometheusNet.Contrib.MongoDb.Events
{
    public record MongoConnectionEvent
    {
        public string Endpoint { get; set; } = string.Empty;

        public int ClusterId { get; set; }

        /// <summary>
        /// Driver-assigned connection identity. Unique per physical connection;
        /// use it (not <see cref="Endpoint"/>) to correlate open/close pairs.
        /// </summary>
        public ConnectionId ConnectionId { get; set; }
    }
}
