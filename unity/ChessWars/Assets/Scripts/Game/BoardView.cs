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
        public void Build(Theme theme = null)
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
            border.transform.localPosition = new Vector3(0, -0.09f, 0);
            border.transform.localScale = new Vector3(8.5f, 0.12f, 8.5f);
            border.GetComponent<Renderer>().sharedMaterial = frame;
            DestroyImmediate(border.GetComponent<Collider>());
            border.isStatic = true;
            markerRoot = new GameObject("Markers").transform;
            markerRoot.SetParent(transform, false);
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
