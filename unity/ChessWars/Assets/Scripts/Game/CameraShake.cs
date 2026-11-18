// Finishing touches on a Cinemachine camera, after it has been placed:
// - the battle's camera shake (Fx.ShakeAmp);
// - the point it looks at (Focus). With both cameras reporting one, the brain blends the aim point along with the
//   position, so the swoop between the board view and a kill close-up keeps the board in frame instead of
//   swinging the view sideways (as the web game lerps its camera target).
using Unity.Cinemachine;
using UnityEngine;

namespace ChessWars.Game
{
    public class CameraShake : CinemachineExtension
    {
        public Vector3 Focus;

        protected override void PostPipelineStageCallback(CinemachineVirtualCameraBase vcam, CinemachineCore.Stage stage, ref CameraState state, float deltaTime)
        {
            if (stage != CinemachineCore.Stage.Finalize) return;
            state.ReferenceLookAt = Focus;
            if (Fx.ShakeAmp <= 0.0001f) return;
            var a = Fx.ShakeAmp;
            state.PositionCorrection += new Vector3(Random.value - 0.5f, Random.value - 0.5f, Random.value - 0.5f) * a;
        }
    }
}
