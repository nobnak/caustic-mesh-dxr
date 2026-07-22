using UnityEngine;

namespace CausticMeshDxr
{
    public interface ICausticReceiverGeometry
    {
        Mesh PrepareMesh();

        GraphicsBuffer VertexBuffer { get; }

        GraphicsBuffer IndexBuffer { get; }

        int VertexStride { get; }

        int PositionOffset { get; }

        bool IsTimeVarying { get; }

        uint TopologyVersion { get; }

        bool DispatchGeometry(
            float time,
            GraphicsBuffer primitiveNormalBuffer,
            int primitiveOffset,
            Matrix4x4 normalToWorld,
            bool forceNormalUpdate);
    }

    public abstract class CausticReceiverGeometry : MonoBehaviour, ICausticReceiverGeometry
    {
        public abstract Mesh PrepareMesh();
        public abstract GraphicsBuffer VertexBuffer { get; }
        public abstract GraphicsBuffer IndexBuffer { get; }
        public abstract int VertexStride { get; }
        public abstract int PositionOffset { get; }
        public abstract bool IsTimeVarying { get; }
        public abstract uint TopologyVersion { get; }
        public abstract bool DispatchGeometry(
            float time,
            GraphicsBuffer primitiveNormalBuffer,
            int primitiveOffset,
            Matrix4x4 normalToWorld,
            bool forceNormalUpdate);
    }
}
