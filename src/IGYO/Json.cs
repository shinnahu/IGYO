using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace IGYO
{
    static class Json
    {
        static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 1000 };

        public static object Parse(string text) => Serializer.DeserializeObject(text);
        public static T Deserialize<T>(string text) => Serializer.Deserialize<T>(text);
        public static string Write(object value) => Serializer.Serialize(value);

        public static Dictionary<string, object> Obj(object value) => value as Dictionary<string, object>;
        public static object[] Arr(object value) => value as object[] ?? (value is ArrayList list ? list.ToArray() : new object[0]);
        public static object Get(Dictionary<string, object> obj, string key) => obj != null && obj.TryGetValue(key, out var v) ? v : null;
        public static string Str(Dictionary<string, object> obj, string key) => Get(obj, key) as string;
    }
}
