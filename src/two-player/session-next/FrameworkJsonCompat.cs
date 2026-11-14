// Only compiled into the .NET 6 plugin. The launcher compiles the exact same
// transaction/client source against the real .NET Framework serializer.
namespace System.Web.Script.Serialization
{
    internal sealed class JavaScriptSerializer
    {
        public int MaxJsonLength{get;set;}=32768;
        public int RecursionLimit{get;set;}=12;
        public object DeserializeObject(string value)
        {
            if(value==null||value.Length>MaxJsonLength)throw new System.IO.InvalidDataException("JSON too large");
            using(var json=System.Text.Json.JsonDocument.Parse(value,new System.Text.Json.JsonDocumentOptions{MaxDepth=RecursionLimit}))
                return Convert(json.RootElement);
        }
        static object Convert(System.Text.Json.JsonElement element)
        {
            switch(element.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    var map=new System.Collections.Generic.Dictionary<string,object>();
                    foreach(var property in element.EnumerateObject())map.Add(property.Name,Convert(property.Value));return map;
                case System.Text.Json.JsonValueKind.Array:
                    var list=new System.Collections.Generic.List<object>();foreach(var item in element.EnumerateArray())list.Add(Convert(item));return list.ToArray();
                case System.Text.Json.JsonValueKind.String:return element.GetString();
                case System.Text.Json.JsonValueKind.Number:
                    if(element.TryGetInt32(out int integer))return integer;
                    if(element.TryGetInt64(out long large))return large;
                    return element.GetDouble();
                case System.Text.Json.JsonValueKind.True:return true;
                case System.Text.Json.JsonValueKind.False:return false;
                case System.Text.Json.JsonValueKind.Null:return null;
                default:throw new System.IO.InvalidDataException("Invalid JSON value");
            }
        }
        public string Serialize(object value)=>System.Text.Json.JsonSerializer.Serialize(value);
    }
}
