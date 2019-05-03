using System;
namespace AppGWBEHealthVMSS.shared
{
    public class ConnectionInfo
    {
        public int? CurrentConnections { get; set; }
        public int? TotalRequests { get; set; }
        public int? ResponseStatus { get; set; }

        public override string ToString() 
        {
            return $"ConnectionInfo: CurrentConnections={CurrentConnections}, TotalRequests={TotalRequests}, ResponseStatus={ResponseStatus}";
        }
    }
}
