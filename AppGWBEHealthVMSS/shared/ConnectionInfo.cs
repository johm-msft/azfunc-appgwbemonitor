using System;
using System.Collections.Generic;
using System.Linq;

namespace AppGWBEHealthVMSS.shared
{
    /// <summary>
    /// Class containing connection information from the app gateway metrics
    /// </summary>
    public class ConnectionInfo
    {
        /// <summary>
        /// Gets or sets the current connections.
        /// </summary>
        /// <value>The current connections.</value>
        public int? CurrentConnections { get; set; }
        /// <summary>
        /// Gets or sets the total requests.
        /// </summary>
        /// <value>The total requests.</value>
        public int? TotalRequests { get; set; }
        /// <summary>
        /// Gets or sets the response status.
        /// </summary>
        /// <value>The response status.</value>
        public int? ResponseStatus { get; set; }

        /// <summary>
        /// Gets or sets the historical data about concurrent connections.
        /// </summary>
        /// <value>The historical data about concurrent connections.</value>
        public List<double?> HistoricalConcurrentConnections { get; set; }
        /// <summary>
        /// Gets or sets the historical total requests.
        /// </summary>
        /// <value>The historical total requests.</value>
        public List<double?> HistoricalTotalRequests { get; set; }
        /// <summary>
        /// Gets or sets the historical data about response status.
        /// </summary>
        /// <value>The historical response status.</value>
        public List<double?> HistoricalResponseStatus { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="T:AppGWBEHealthVMSS.shared.ConnectionInfo"/> class.
        /// </summary>
        public ConnectionInfo()
        {
            HistoricalConcurrentConnections = new List<double?>();
            HistoricalTotalRequests = new List<double?>();
            HistoricalResponseStatus = new List<double?>();
        }

        /// <summary>
        /// Returns a <see cref="T:System.String"/> that represents the current <see cref="T:AppGWBEHealthVMSS.shared.ConnectionInfo"/>.
        /// </summary>
        /// <returns>A <see cref="T:System.String"/> that represents the current <see cref="T:AppGWBEHealthVMSS.shared.ConnectionInfo"/>.</returns>
        public override string ToString()
        {
            return $"ConnectionInfo: CurrentConnections={CurrentConnections}, TotalRequests={TotalRequests}, ResponseStatus={ResponseStatus}";
        }

        /// <summary>
        /// Gets the history as string.
        /// </summary>
        /// <returns>The history as string.</returns>
        public string GetHistoryAsString()
        {
            return $@"History: 
                ConcurrentConnections : {string.Join(",", HistoricalConcurrentConnections.Select(v => v.HasValue ? v.Value.ToString() : "null"))}
                TotalRequests : {string.Join(",", HistoricalTotalRequests.Select(v => v.HasValue ? v.Value.ToString() : "null"))}
                ResponseStatus : {string.Join(",", HistoricalResponseStatus.Select(v => v.HasValue ? v.Value.ToString() : "null"))} ";
        }
    }
}
