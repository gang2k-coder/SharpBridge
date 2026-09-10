// CaptureDebuggee — synthetic capture payloads for get_captures_v2 tests.
// ALL data here is fictional (generic "channel/widget" shape, invented ids).
// It deliberately reproduces the v1 pain points with harmless stand-ins:
//   - one large (~61 KB) JSON-string local (payloadJson)
//   - scalar locals incl. a null reference (referenceCurve = null)
//   - a short loop so capture breakpoints fire several times

using System.Text;

Console.WriteLine($"PID: {Environment.ProcessId}");
Console.WriteLine("Press ENTER to start...");
Console.ReadLine();

int counter = 0;
string referenceCurveId = "REF-ALPHA (SIM)";
object? referenceCurve = null;
string sourceMnemonic = "SRC-01";
string payloadJson = BuildSyntheticPayload();

Console.WriteLine($"payload bytes: {Encoding.UTF8.GetByteCount(payloadJson)}");

for (int i = 0; i < 4; i++)
{
    counter++;
    referenceCurveId = $"REF-{(char)('A' + i)} (SIM)";
    Console.WriteLine($"iter {i}, counter={counter}, ref={referenceCurveId}");
}

LoopEnd.Signal();

Console.WriteLine("Done. Press ENTER to exit...");
Console.ReadLine();

static string BuildSyntheticPayload()
{
    var sb = new StringBuilder();
    sb.Append("{\n  \"content\": {\n    \"widget\": {\n      \"tracks\": [\n");
    for (int t = 0; t < 2; t++)
    {
        sb.Append($"        {{ \"trackId\": \"TRACK-{t:D2}\", \"channels\": [\n");
        for (int c = 0; c < 5; c++)
        {
            var id = t * 5 + c;
            var pad = new string('x', 6000); // filler — makes the blob ~61 KB
            if (c > 0) sb.Append(",\n");
            sb.Append($"          {{ \"mnemonic\": \"CH-{id:D2}\", \"name\": \"channel-{id:D2}\", \"uniqueId\": \"UID-CH-{id:D2}\", \"filler\": \"{pad}\", \"fills\": [ {{ \"refLineId\": \"REF-{(char)('A' + id)} (SIM)\", \"refLineName\": \"line-{id:D2}\" }} ], \"bindings\": {{ \"toolPath\": \"tools/path-{id:D2}\", \"samplingRate\": {id} }} }}");
        }
        sb.Append($"\n        ] }}{(t < 1 ? ",\n" : "\n")}");
    }
    sb.Append("      ]\n      }\n    }\n  }\n");
    var json = sb.ToString();
    System.Text.Json.Nodes.JsonNode.Parse(json); // self-check: an invalid payload must fail fast
    return json;
}
