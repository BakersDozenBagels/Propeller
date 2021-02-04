using RT.PropellerApi;
using RT.Servers;
using RT.Json;

namespace RT.Propeller.Modules
{
    class SimpleDB : PropellerModuleBase<SimpleDBSettings>
    {
        public int count = 0;

        public override string Name { get { return "SimpleDB"; } }

        public override HttpResponse Handle(HttpRequest req)
        {
            if (req.Headers.ContainsKey("SIMPLEDBADD"))
                if (req.Headers["SIMPLEDBADD"] == "TRUE")
                    count++;

            if (req.Headers.ContainsKey("SIMPLEDBSUB"))
                if (req.Headers["SIMPLEDBSUB"] == "TRUE")
                    count--;
            if (count < 0)
                count = 0;

            var dict = new JsonDict();
            dict.Add("Count", JsonNumber.Create(count));
            return HttpResponse.Json(dict);
        }
    }

    /// <summary>Contains the settings for the SimpleDB Propeller module.</summary>
    public sealed class SimpleDBSettings
    {
        //Nothing here yet.
    }
}
