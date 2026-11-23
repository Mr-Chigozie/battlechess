// Invite links open the app: adds to the game's activity in the generated Android manifest
//   - App Links for https://chesswars-server.onrender.com/i/CODE (verified against the server's
//     /.well-known/assetlinks.json, so a tap in WhatsApp goes straight to the app when it's installed), and
//   - the chesswars://invite/CODE scheme, which works without verification (testing, other launchers).
// The app reads the link with Application.absoluteURL / deepLinkActivated (OnlineUi.OpenLink).
using System.IO;
using System.Linq;
using System.Xml;
using UnityEditor.Android;

namespace ChessWars.EditorTools
{
    public class AndroidLinks : IPostGenerateGradleAndroidProject
    {
        const string Android = "http://schemas.android.com/apk/res/android";
        const string Host = "chesswars-server.onrender.com";

        public int callbackOrder => 10;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            string file = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            var doc = new XmlDocument();
            doc.Load(file);
            var activity = doc.SelectNodes("/manifest/application/activity").Cast<XmlElement>()
                .FirstOrDefault(a => a.InnerXml.Contains("android.intent.category.LAUNCHER"));
            if (activity == null || activity.InnerXml.Contains(Host)) return;

            void Set(XmlElement e, string name, string value)
            {
                var a = doc.CreateAttribute("android", name, Android);
                a.Value = value;
                e.Attributes.Append(a);
            }
            XmlElement Child(XmlElement parent, string name, params (string attr, string value)[] attrs)
            {
                var e = doc.CreateElement(name);
                foreach (var (attr, value) in attrs) Set(e, attr, value);
                parent.AppendChild(e);
                return e;
            }
            XmlElement Filter(bool verify)
            {
                var f = doc.CreateElement("intent-filter");
                if (verify) Set(f, "autoVerify", "true");
                activity.AppendChild(f);
                Child(f, "action", ("name", "android.intent.action.VIEW"));
                Child(f, "category", ("name", "android.intent.category.DEFAULT"));
                Child(f, "category", ("name", "android.intent.category.BROWSABLE"));
                return f;
            }
            var web = Filter(true);
            Child(web, "data", ("scheme", "https"), ("host", Host), ("pathPrefix", "/i/"));
            var custom = Filter(false);
            Child(custom, "data", ("scheme", "chesswars"), ("host", "invite"));
            doc.Save(file);
        }
    }
}
