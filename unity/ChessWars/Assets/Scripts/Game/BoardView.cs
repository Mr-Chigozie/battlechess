// The board: 64 squares, a wooden frame and the selection / move markers. Squares are 1 unit, centred on the origin.
using System.Collections.Generic;
using UnityEngine;

namespace ChessWars.Game
{
    public class BoardView : MonoBehaviour
    {
        public Material lightSquare, darkSquare, frame, selectMarker, moveMarker, captureMarker, lastMoveMarker;

        readonly List<GameObject> markers = new List<GameObject>();
        Transform markerRoot;

        // 64 raised slabs on a frame. Their tops sit at y = 0, the plane units stand on and taps are read from.
        public void Build(Theme theme = null, char side = 'w')
        {
            if (theme != null)
            {
                lightSquare.SetColor("_BaseColor", theme.Light);
                darkSquare.SetColor("_BaseColor", theme.Dark);
                frame.SetColor("_BaseColor", theme.Trim);
            }
            for (int i = transform.childCount - 1; i >= 0; i--) DestroyImmediate(transform.GetChild(i).gameObject);
            var squares = new GameObject("Squares").transform;
            squares.SetParent(transform, false);
            for (int row = 0; row < 8; row++)
                for (int col = 0; col < 8; col++)
                {
                    var tile = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    tile.name = Rules.Rules.Algebraic(row, col);
                    tile.transform.SetParent(squares, false);
                    tile.transform.localPosition = Armies.SquareToWorld(row, col) + Vector3.down * 0.05f;
                    tile.transform.localScale = new Vector3(0.97f, 0.1f, 0.97f);
                    tile.GetComponent<Renderer>().sharedMaterial = (row + col) % 2 == 0 ? lightSquare : darkSquare;
                    DestroyImmediate(tile.GetComponent<Collider>());  // taps hit the board plane, not tiles
                    tile.isStatic = true;
                }
            var border = GameObject.CreatePrimitive(PrimitiveType.Cube);
            border.name = "Frame";
            border.transform.SetParent(transform, false);
            border.transform.localPosition = new Vector3(0, -0.08f, 0);   // top at -0.02, just above the platform trim
            border.transform.localScale = new Vector3(9.3f, 0.12f, 9.3f);
            border.GetComponent<Renderer>().sharedMaterial = frame;
            DestroyImmediate(border.GetComponent<Collider>());
            border.isStatic = true;
            Labels(side);
            markerRoot = new GameObject("Markers").transform;
            markerRoot.SetParent(transform, false);
        }

        // a-h along the near and far edges, 1-8 down both sides, in gold on the frame, as the web board has them.
        // They read the right way up from the side the player leads.
        void Labels(char side)
        {
            var root = new GameObject("Labels").transform;
            root.SetParent(transform, false);
            var font = Ui.Serif;
            // The default 3D-text shader draws over everything; the UI shader with a depth test lets the units hide
            // the letters behind them. It reads the font's alpha-only atlas as white, tinted by the text colour.
            font.RequestCharactersInTexture("abcdefgh12345678", 64, FontStyle.Bold);
            var material = new Material(Shader.Find("UI/Default")) { name = "Board labels" };
            material.mainTexture = font.material.mainTexture;
            material.SetVector("_TextureSampleAdd", new Vector4(1, 1, 1, 0));
            material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            Font.textureRebuilt += rebuilt => { if (rebuilt == font && material != null) material.mainTexture = font.material.mainTexture; };
            void Put(string text, float x, float z)
            {
                var go = new GameObject(text);
                go.transform.SetParent(root, false);
                go.transform.localPosition = new Vector3(x, -0.015f, z);
                go.transform.localRotation = Quaternion.Euler(90f, side == 'w' ? 180f : 0f, 0f);
                var mesh = go.AddComponent<TextMesh>();
                mesh.font = font;
                mesh.text = text;
                mesh.fontSize = 64;
                mesh.characterSize = 0.045f;
                mesh.anchor = TextAnchor.MiddleCenter;
                mesh.alignment = TextAlignment.Center;
                mesh.fontStyle = FontStyle.Bold;
                mesh.color = Armies.Hex(0xd9bf8a);
                var r = go.GetComponent<MeshRenderer>();
                r.sharedMaterial = material;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            for (int i = 0; i < 8; i++)
            {
                var file = ((char)('a' + i)).ToString();
                var fileX = Armies.SquareToWorld(0, i).x;
                Put(file, fileX, 4.32f);
                Put(file, fileX, -4.32f);
                var rank = (8 - i).ToString();
                var rankZ = Armies.SquareToWorld(i, 0).z;
                Put(rank, 4.32f, rankZ);
                Put(rank, -4.32f, rankZ);
            }
        }

        public void ClearMarkers()
        {
            foreach (var m in markers) Destroy(m);
            markers.Clear();
        }

        public void Mark(int row, int col, Material material, float size = 0.94f, float lift = 0.004f)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(quad.GetComponent<Collider>());
            quad.transform.SetParent(markerRoot, false);
            quad.transform.localPosition = Armies.SquareToWorld(row, col) + Vector3.up * lift;
            quad.transform.localRotation = Quaternion.Euler(90, 0, 0);
            quad.transform.localScale = Vector3.one * size;
            var r = quad.GetComponent<Renderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            markers.Add(quad);
        }

        // The square under a screen point, or null when the point is off the board.
        public static Vector2Int? SquareAt(Camera camera, Vector2 screen)
        {
            var ray = camera.ScreenPointToRay(screen);
            if (!new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float distance)) return null;
            var p = ray.GetPoint(distance);
            int col = Mathf.FloorToInt(4f - p.x), row = Mathf.FloorToInt(p.z + 4f);   // inverse of Armies.SquareToWorld
            return row >= 0 && row < 8 && col >= 0 && col < 8 ? new Vector2Int(col, row) : (Vector2Int?)null;
        }
    }
}
