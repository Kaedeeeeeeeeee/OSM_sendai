using UnityEngine;

namespace OsmSendai.World
{
    public sealed class BillboardLabel : MonoBehaviour
    {
        private void LateUpdate()
        {
            var cam = Camera.main;
            if (cam != null)
                transform.forward = cam.transform.forward;
        }
    }
}
