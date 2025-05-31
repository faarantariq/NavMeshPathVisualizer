using UnityEngine;
using UnityEngine.AI;

namespace MFT
{
    [RequireComponent(typeof(LineRenderer))]
    public class NavMeshPathVisualizer : MonoBehaviour
    {
        [Header("References")] public Transform player;
        public Transform target;

        [Header("Line Settings"), Range(1, 50)]
        public int smoothness = 10; // How smooth/curvy the line should be

        public float lineWidth = 0.12f;
        public Color lineColor = Color.green;

        [Header("Visual Offset")] public float yOffset = 0.2f; // Raises the line for visibility
        public bool projectToSurface = true;

        [Header("Raycast")] public float raycastUp = 1.0f; // How high above each point to start the ground check
        public float raycastDown = 5f; // How down each point to start the ground check
        public LayerMask surfaceMask = ~0;

        [Header("Smoothing Options"), Range(0, 45)]
        public float minCornerAngle = 10f; // Ignore small bends for smoothing

        [Range(0.1f, 1f)] public float curveTension = 0.5f; // How tight/loose the curves are

        [Header("Endpoint Margins")] public float endMargin = 1.0f;

        [Header("Texture Tiling")] public bool updateArrowTiling = true; // Check for update texture tile in shader
        public float tileLength = 1f; // For texture scaling

        [Header("Texture Scroll For One Time")]
        public KeyCode keycode = KeyCode.Mouse0; // Key for path visualization

        public bool oneTimeScroll = false;
        public float scrollOffset = 0f;
        public float scrollSpeed = 1f;
        public float maxScrollOffset = 2f;

        [Header("Path Visibility")] public float visibleDuration = 2f;

        private LineRenderer _lineRenderer;

        private NavMeshPath _navMeshPath;

        // Buffers: reused, no garbage!
        private Vector3[] _cachedCorners = new Vector3[16]; // Raw corners
        private int _cornerCount = 0;

        private Vector3[] _filteredCorners = new Vector3[16]; // Filtered corners
        private Vector3[] _smoothedPoints = new Vector3[256]; // Final curve
        private int _smoothedCount = 0;

        private float _updateTimer = 0f;
        private const float PathUpdateInterval = 0.2f;

        private Vector3 _lastPlayerPos, _lastTargetPos;

        private bool _showPath = false;
        private float _visibleTimer = 0f;
        private static readonly int Tiling = Shader.PropertyToID("_Tiling");

        private void Awake()
        {
            _lineRenderer = GetComponent<LineRenderer>();
            _lineRenderer.startWidth = lineWidth;
            _lineRenderer.endWidth = lineWidth / 2f;
            _lineRenderer.endColor = lineColor;
            _lineRenderer.useWorldSpace = true;
            scrollOffset = 0f;
            _navMeshPath = new NavMeshPath();
            _lineRenderer.positionCount = 0;
            _lastPlayerPos = Vector3.positiveInfinity;
            _lastTargetPos = Vector3.positiveInfinity;
        }

        private void Update()
        {
            // --- One Time Scroll Mode ---
            if (oneTimeScroll)
            {
                if (Input.GetKeyDown(keycode))
                {
                    _showPath = true;
                    scrollOffset = 0f;
                    _visibleTimer = visibleDuration;
                }

                if (_showPath)
                {
                    _visibleTimer -= Time.deltaTime;
                    if (_visibleTimer <= 0f)
                    {
                        _showPath = false;
                        _lineRenderer.positionCount = 0;
                        return;
                    }
                }
                else
                {
                    _lineRenderer.positionCount = 0;
                    return;
                }

                scrollOffset -= Time.deltaTime * scrollSpeed;
                if (Mathf.Abs(scrollOffset) >= maxScrollOffset)
                    scrollOffset = -maxScrollOffset;
            }
            else
            {
                _showPath = true;
                scrollOffset -= Time.deltaTime * scrollSpeed;
            }

            // --- Path Update Interval ---
            // Only update path if something moved or timer passed
            _updateTimer += Time.deltaTime;
            if (_updateTimer < PathUpdateInterval && !PositionsMovedSignificantly())
                return;

            _updateTimer = 0f;

            TryUpdatePath();
        }

        private bool PositionsMovedSignificantly(float threshold = 0.05f)
        {
            if (player == null || target == null) return false;
            return Vector3.Distance(_lastPlayerPos, player.position) > threshold ||
                   Vector3.Distance(_lastTargetPos, target.position) > threshold;
        }

        private void TryUpdatePath()
        {
            // NavMesh pathfinding
            if (player == null || target == null)
            {
                _lineRenderer.positionCount = 0;
                return;
            }

            var navMeshTargetPos = GetNavMeshPosition(target.position, 2f);
            var found = NavMesh.CalculatePath(player.position, navMeshTargetPos, NavMesh.AllAreas, _navMeshPath);

            if (!found)
            {
                _lineRenderer.positionCount = 0;
                return;
            }

            // Copy corners to buffer
            var corners = _navMeshPath.corners;
            _cornerCount = Mathf.Min(corners.Length, _cachedCorners.Length);
            for (int i = 0; i < _cornerCount; i++)
                _cachedCorners[i] = corners[i];

            if (_cornerCount < 2)
            {
                _lineRenderer.positionCount = 0;
                return;
            }

            // Smooth and filter
            var filteredCount = FilterCorners(_cachedCorners, _cornerCount, minCornerAngle, _filteredCorners);

            _smoothedCount = SmoothPath(_filteredCorners, filteredCount, smoothness, curveTension, _smoothedPoints);

            if (projectToSurface)
                ProjectPointsToSurface(_smoothedPoints, _smoothedCount);

            // END margin.. Shorten before target
            if (endMargin > 0f && _smoothedCount >= 2)
            {
                var dir = (_smoothedPoints[_smoothedCount - 1] - _smoothedPoints[_smoothedCount - 2]).normalized;
                var dist = Vector3.Distance(_smoothedPoints[_smoothedCount - 2], _smoothedPoints[_smoothedCount - 1]);
                var margin = Mathf.Clamp(endMargin, 0f, dist - 0.01f);
                _smoothedPoints[_smoothedCount - 1] = _smoothedPoints[_smoothedCount - 1] - dir * margin;
            }

            // Final Y offset for line visibility
            for (var i = 0; i < _smoothedCount; i++)
                _smoothedPoints[i].y += yOffset;
            // Draw!
            _lineRenderer.positionCount = _smoothedCount;
            _lineRenderer.SetPositions(_smoothedPoints);

            // Tiling
            if (updateArrowTiling)
                UpdateLineTiling();

            _lastPlayerPos = player.position;
            _lastTargetPos = target.position;
        }

        private Vector3 GetNavMeshPosition(Vector3 pos, float maxDist)
        {
            // Gets nearest valid NavMesh point (in case target is off-mesh)
            return NavMesh.SamplePosition(pos, out var hit, maxDist, NavMesh.AllAreas) ? hit.position : pos;
        }

        private int FilterCorners(Vector3[] corners, int count, float minAngleDeg, Vector3[] buffer)
        {
            // Removes corners with too small of an angle (reduces sharp bends)
            if (count < 3)
            {
                for (int i = 0; i < count; i++) buffer[i] = corners[i];
                return count;
            }

            var filtered = 0;
            buffer[filtered++] = corners[0];
            for (int i = 1; i < count - 1; i++)
            {
                var prev = corners[i - 1];
                var curr = corners[i];
                var next = corners[i + 1];
                var angle = Vector3.Angle(next - curr, curr - prev);
                if (angle > minAngleDeg)
                    buffer[filtered++] = curr;
            }

            buffer[filtered++] = corners[count - 1];
            return filtered;
        }

        private int SmoothPath(Vector3[] corners, int count, int segmentsPerCorner, float tension, Vector3[] buffer)
        {
            // Makes the path look smooth/curvy using Catmull-Rom splines
            if (count < 2)
            {
                for (var i = 0; i < count; i++) buffer[i] = corners[i];
                return count;
            }

            var idx = 0;
            for (var i = 0; i < count - 1; i++)
            {
                var p0 = i == 0 ? corners[i] : corners[i - 1];
                var p1 = corners[i];
                var p2 = corners[i + 1];
                var p3 = (i + 2 < count) ? corners[i + 2] : corners[i + 1];

                for (var j = 0; j < segmentsPerCorner; j++)
                {
                    var t = j / (float)segmentsPerCorner;
                    buffer[idx++] = CatmullRom(p0, p1, p2, p3, t, tension);
                }
            }

            buffer[idx++] = corners[count - 1];
            return idx;
        }

        private Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t, float tension)
        {
            // Math for smooth curves through points
            var t2 = t * t;
            var t3 = t2 * t;
            return (2 * p1 +
                    (-p0 + p2) * t * tension +
                    (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 * tension +
                    (-p0 + 3 * p1 - 3 * p2 + p3) * t3 * tension) * 0.5f;
        }

        private void ProjectPointsToSurface(Vector3[] points, int count)
        {
            // Drops each point onto the surface below it
            for (var i = 0; i < count; i++)
            {
                var rayOrigin = points[i] + Vector3.up * raycastUp;
                if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, raycastDown + raycastUp, surfaceMask))
                {
                    points[i].y = hit.point.y;
                }
#if UNITY_EDITOR
                else
                {
                    Debug.DrawRay(rayOrigin, Vector3.down * (raycastDown + raycastUp), Color.red, 1.0f);
                }
#endif
            }
        }

        private void UpdateLineTiling()
        {
            // Adjusts material tiling for path length
            if (_lineRenderer.positionCount < 2) return;
            if (!_lineRenderer.material.HasProperty("_Tiling")) return;

            var totalLength = 0f;
            for (int i = 1; i < _lineRenderer.positionCount; i++)
                totalLength += Vector3.Distance(_lineRenderer.GetPosition(i - 1), _lineRenderer.GetPosition(i));

            var tilingX = totalLength / tileLength;
            _lineRenderer.material.SetVector(Tiling, new Vector4(tilingX, 1f, 0f, 0f));
        }
    }
}