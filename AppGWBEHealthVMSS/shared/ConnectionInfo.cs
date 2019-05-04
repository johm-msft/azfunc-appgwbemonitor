using System;
using System.Collections.Generic;
using System.Linq;

namespace AppGWBEHealthVMSS.shared
{
    public class ConnectionInfo
    {
        public int? CurrentConnections { get; set; }
        public int? TotalRequests { get; set; }
        public int? ResponseStatus { get; set; }

        public List<double?> HistoricalConcurrentConnections { get; set; }
        public List<double?> HistoricalTotalRequests { get; set; }
        public List<double?> HistoricalResponseStatus { get; set; }

        public ConnectionInfo()
        {
            HistoricalConcurrentConnections = new List<double?>();
            HistoricalTotalRequests = new List<double?>();
            HistoricalResponseStatus = new List<double?>();
        }

        public override string ToString()
        {
            return $"ConnectionInfo: CurrentConnections={CurrentConnections}, TotalRequests={TotalRequests}, ResponseStatus={ResponseStatus}";
        }

        public string GetHistoryAsString()
        {
            return $@"History: 
                ConcurrentConnections : {string.Join(",", HistoricalConcurrentConnections.Select(v => v.HasValue ? v.Value.ToString() : "null"))}
                TotalRequests : {string.Join(",", HistoricalTotalRequests.Select(v => v.HasValue ? v.Value.ToString() : "null"))}
                ResponseStatus : {string.Join(",", HistoricalResponseStatus.Select(v => v.HasValue ? v.Value.ToString() : "null"))} ";
        }
    }
}
