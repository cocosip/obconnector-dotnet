namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    internal static class GroupBlacklistFixture
    {
        internal static string Tns(int first, int second, int? cap = 3, int threshold = 2, int timeout = 50, bool global = true, int? addressThreshold = null)
            => "APP=(DESCRIPTION=(OBLB_STRATEGY=ROTATION)" + (cap.HasValue ? "(OBLB_RETRY_ALL_DOWNS=" + cap + ")" : "") +
            (global ? Policy(1, 50) : "") + "(ADDRESS_LIST=" + Policy(threshold, timeout) + Address(first, addressThreshold) + ")(ADDRESS_LIST=" + Address(second) + "))";

        internal static string Json(int first, int second, int? cap = 3, int threshold = 2, int timeout = 50, bool global = true, int? addressThreshold = null)
            => "{\"data\":{\"NET_SERVICE_NAME\":\"APP\",\"DESCRIPTION\":{\"OBLB_STRATEGY\":\"ROTATION\"," +
            (cap.HasValue ? "\"OBLB_RETRY_ALL_DOWNS\":" + cap + "," : "") + (global ? JsonPolicy(1, 50) + "," : "") +
            "\"ADDRESS_LIST\":[{" + JsonPolicy(threshold, timeout) + ",\"ADDRESS\":[" + JsonAddress(first, addressThreshold) + "]},{\"ADDRESS\":[" + JsonAddress(second) + "]}]}}}";

        private static string Policy(int threshold, int timeout) => "(OBLB_BLACKLIST=(REMOVE_STRATEGY=(NAME=TIMEOUT)(TIMEOUT=" + timeout + "))" +
            "(APPEND_STRATEGY=(NAME=" + (threshold == 1 ? "NORMAL" : "RETRYDURATION") + ")" +
            (threshold == 1 ? "" : "(RETRYTIMES=" + threshold + ")(DURATION=2147483647)") + "))";
        private static string JsonPolicy(int threshold, int timeout) => "\"OBLB_BLACKLIST\":{\"REMOVE_STRATEGY\":{\"NAME\":\"TIMEOUT\",\"TIMEOUT\":" + timeout + "}," +
            "\"APPEND_STRATEGY\":{\"NAME\":\"" + (threshold == 1 ? "NORMAL" : "RETRYDURATION") + "\"" +
            (threshold == 1 ? "" : ",\"RETRYTIMES\":" + threshold + ",\"DURATION\":2147483647") + "}}";
        private static string Address(int port, int? threshold = null) => "(ADDRESS=(PROTOCOL=TCP)(HOST=127.0.0.1)(PORT=" + port + ")" + (threshold.HasValue ? Policy(threshold.Value, 50) : "") + ")";
        private static string JsonAddress(int port, int? threshold = null) => "{\"PROTOCOL\":\"TCP\",\"HOST\":\"127.0.0.1\",\"PORT\":" + port + (threshold.HasValue ? "," + JsonPolicy(threshold.Value, 50) : "") + "}";
    }
}
