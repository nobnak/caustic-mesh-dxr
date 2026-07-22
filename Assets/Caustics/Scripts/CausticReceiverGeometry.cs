using UnityEngine;

namespace CausticMeshDxr
{
    public readonly struct CausticReceiverGeometryView
    {
        public readonly GraphicsBuffer positionBuffer;
        public readonly GraphicsBuffer indexBuffer;
        public readonly int vertexCount;
        public readonly int primitiveCount;

        public CausticReceiverGeometryView(
            GraphicsBuffer positionBuffer,
            GraphicsBuffer indexBuffer,
            int vertexCount,
            int primitiveCount)
        {
            this.positionBuffer = positionBuffer;
            this.indexBuffer = indexBuffer;
            this.vertexCount = vertexCount;
            this.primitiveCount = primitiveCount;
        }

        public bool IsValid => positionBuffer != null
            && indexBuffer != null
            && vertexCount > 0
            && primitiveCount > 0
            && positionBuffer.count >= vertexCount
            && indexBuffer.count >= primitiveCount * 3;
    }

    public interface ICausticReceiverGeometry
    {
        Mesh PrepareMesh();

        CausticReceiverGeometryView GeometryView { get; }

        bool IsTimeVarying { get; }

        uint TopologyVersion { get; }

        bool DispatchGeometry(float time);
    }

    public abstract class CausticReceiverGeometry : MonoBehaviour, ICausticReceiverGeometry
    {
        public abstract Mesh PrepareMesh();
        public abstract CausticReceiverGeometryView GeometryView { get; }
        public abstract bool IsTimeVarying { get; }
        public abstract uint TopologyVersion { get; }
        public abstract bool DispatchGeometry(float time);
    }
}
