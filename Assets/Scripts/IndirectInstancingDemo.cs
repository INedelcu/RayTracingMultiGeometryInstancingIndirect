using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.UI;

[ExecuteInEditMode]
public class MeshInstancing : MonoBehaviour
{
    [SerializeField] ComputeShader cullingCS;
    [SerializeField] ComputeShader copyIndicesCS;
    [SerializeField] ComputeShader copyVerticesCS;

    [SerializeField] RayTracingShader rayTracingShader;

    [SerializeField] Mesh[] meshes;
    [SerializeField] Material[] materials;

    [SerializeField] Vector2Int instanceGridSize = new Vector2Int(200, 200);

    [SerializeField] Texture envTexture;

    [SerializeField] float cullingRadius = 1000.0f;

    public Text fpsText;
    public Text titleText;

    private float lastRealtimeSinceStartup = 0;
    private float updateFPSTimer = 1.0f;

    private uint cameraWidth = 0;
    private uint cameraHeight = 0;

    private RenderTexture rayTracingOutput;

    private RayTracingAccelerationStructure rtas;

    private RayTracingInstanceData instanceData;

    private GraphicsBuffer instanceDataBuffer;

    // Instance indices can be associate them with other instance data (e.g. Per Instance Color). Filled by Instance Culling compute shader.
    private GraphicsBuffer instanceIndices;

    private GraphicsBuffer indirectArgsBuffer;

    // Common vertex and index buffers that hold various geometries at different ranges.
    private GraphicsBuffer vertexBuffer;
    private GraphicsBuffer indexBuffer;

    private RayTracingMultiGeometryInstanceConfig instancesConfig = new RayTracingMultiGeometryInstanceConfig();

    private void ReleaseResources()
    {
        if (rtas != null)
        {
            rtas.Release();
            rtas = null;
        }

        if (rayTracingOutput)
        {
            rayTracingOutput.Release();
            rayTracingOutput = null;
        }

        cameraWidth = 0;
        cameraHeight = 0;

        if (instanceData != null)
        {
            instanceData.Dispose();
            instanceData = null;
        }

        if (instanceIndices != null)
        {
            instanceIndices.Release();
            instanceIndices = null;
        }

        if (indirectArgsBuffer != null)
        {
            indirectArgsBuffer.Release();
            indirectArgsBuffer = null;
        }

        if (vertexBuffer != null)
        {
            vertexBuffer.Release();
            vertexBuffer = null;
        }

        if (indexBuffer != null)
        {
            indexBuffer.Release();
            indexBuffer = null;
        }

        if (instanceDataBuffer != null)
        {
            instanceDataBuffer.Release();
            instanceDataBuffer = null;
        }
    }

    // Custom ray tracing instance data. Can have any format but it must contain the following fields: objectToWorld, materialIndex and geometryIndex.
    public struct RayTracingPerInstanceData
    {
        public Matrix4x4 objectToWorld;
        public uint something;
        public uint materialIndex;
        public uint geometryIndex;
        public uint stuff;
    };
    private void CreateResources()
    {
        if (cameraWidth != Camera.main.pixelWidth || cameraHeight != Camera.main.pixelHeight)
        {
            if (rayTracingOutput)
                rayTracingOutput.Release();

            rayTracingOutput = new RenderTexture(Camera.main.pixelWidth, Camera.main.pixelHeight, 0, RenderTextureFormat.ARGBHalf);
            rayTracingOutput.enableRandomWrite = true;
            rayTracingOutput.Create();

            cameraWidth = (uint)Camera.main.pixelWidth;
            cameraHeight = (uint)Camera.main.pixelHeight;
        }

        if (instanceData == null || instanceData.columns != instanceGridSize.x || instanceData.rows != instanceGridSize.y)
        {
            if (instanceData != null)
            {
                instanceData.Dispose();
            }

            instanceData = new RayTracingInstanceData(instanceGridSize.x, instanceGridSize.y);

            if (instanceIndices != null)
            {
                instanceIndices.Release();
                instanceIndices = null;
            }
        }

        // Instance indices that were added to the RTAS. Filled in Culling.compute.
        if (instanceIndices == null)
        {
            instanceIndices = new GraphicsBuffer(GraphicsBuffer.Target.Append, instanceData.matrices.Length, UnsafeUtility.SizeOf(typeof(uint)));
        }

        if (indirectArgsBuffer == null)
        {
            indirectArgsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 1, 2 * UnsafeUtility.SizeOf(typeof(uint)));
            uint[] ints = new uint[2]
            {
                0,
                (uint)instanceData.matrices.Length,
            };

            indirectArgsBuffer.SetData(ints);
        }

        int totalVertexCount = 0;
        int totalIndexCount = 0;
        int vertexSize = 0;

        if (meshes.Length > 0)
        {
            vertexSize = meshes[0].GetVertexBufferStride(0);

            instancesConfig.subGeometries = new RayTracingSubGeometryDesc[meshes.Length];
            instancesConfig.materials = materials;
            instancesConfig.rayTracingMode = UnityEngine.Experimental.Rendering.RayTracingMode.Static;
            instancesConfig.enableTriangleCulling = false;
            instancesConfig.frontTriangleCounterClockwise = true;
            instancesConfig.vertexAttributes = meshes[0].GetVertexAttributes();

            for (int i = 0; i < meshes.Length; i++)
            {
                ref Mesh mesh = ref meshes[i];
                totalVertexCount += mesh.vertexCount;

                if (mesh.subMeshCount != 1)
                {
                    instancesConfig.subGeometries = null;
                    Debug.Log("Only meshes with 1 sub-mesh are supported - " + mesh.name);
                }

                if (vertexSize != mesh.GetVertexBufferStride(0))
                {
                    instancesConfig.subGeometries = null;
                    Debug.Log("Different vertex sizes is not supported - " + mesh.name);
                }

                if (mesh.GetTopology(0) != MeshTopology.Triangles)
                {
                    instancesConfig.subGeometries = null;
                    Debug.Log("Mesh topology is not supported. Only triangle topology is supported - " + mesh.name);
                }
            }

            if (instancesConfig.subGeometries != null)
            {
                for (int i = 0; i < meshes.Length; i++)
                {
                    ref Mesh mesh = ref meshes[i];

                    instancesConfig.subGeometries[i] = new RayTracingSubGeometryDesc()
                    {
                        indexStart = totalIndexCount,
                        indexCount = (int)mesh.GetIndexCount(0),
                        vertexStart = 0,
                        vertexCount = totalVertexCount,
                        flags = RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly,
                        id = 0
                    };

                    totalIndexCount += (int)mesh.GetIndexCount(0);
                }
            }
        }
        else
        {
            instancesConfig.subGeometries = null;

            if (vertexBuffer != null)
                vertexBuffer.Release();

            if (indexBuffer != null)
                indexBuffer.Release();
        }

        if (vertexBuffer != null && totalVertexCount != vertexBuffer.count)
        {
            vertexBuffer.Release();
            vertexBuffer = null;
        }

        if (indexBuffer != null && totalIndexCount != indexBuffer.count)
        {
            indexBuffer.Release();
            indexBuffer = null;
        }

        if (totalVertexCount > 0 && totalIndexCount > 0 && vertexSize > 0 && instancesConfig.subGeometries != null && vertexBuffer == null)
        {
            if (vertexSize % 4 != 0)
                Debug.Log("Vertex size must be a multiple of 4.");

            vertexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, totalVertexCount, vertexSize);

            // Use 32-bit indices in the final index buffer.
            indexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, totalIndexCount, sizeof(uint));

            instancesConfig.vertexBuffer = vertexBuffer;
            instancesConfig.indexBuffer = indexBuffer;

            if (copyIndicesCS != null && copyVerticesCS != null)
            {
                int baseVertex = 0;

                // Copy the vertex and index data of each mesh into the common vertex and index buffers.
                for (uint i = 0; i < instancesConfig.subGeometries.Length; i++)
                {
                    ref RayTracingSubGeometryDesc desc = ref instancesConfig.subGeometries[i];

                    ref Mesh mesh = ref meshes[i];

                    GraphicsBuffer meshIndexBuffer = mesh.GetIndexBuffer();
                    GraphicsBuffer meshVertexBuffer = mesh.GetVertexBuffer(0);

                    copyIndicesCS.SetBuffer(0, "InputIndexBuffer", meshIndexBuffer);
                    copyIndicesCS.SetInt("InputIndexBufferStride", meshIndexBuffer.stride);
                    copyIndicesCS.SetInt("InputIndexCount", meshIndexBuffer.count);
                    copyIndicesCS.SetInt("BaseVertex", baseVertex);

                    copyIndicesCS.SetBuffer(0, "OutputIndexBuffer", indexBuffer);
                    copyIndicesCS.SetInt("OutputIndexBufferOffset", desc.indexStart * sizeof(uint));

                    int threadGroupsX = ((meshIndexBuffer.count / 3) + 64 - 1) / 64;
                    copyIndicesCS.Dispatch(0, threadGroupsX, 1, 1);

                    baseVertex += meshVertexBuffer.count;

                    meshIndexBuffer.Release();
                    meshVertexBuffer.Release();
                }

                baseVertex = 0;

                for (uint i = 0; i < instancesConfig.subGeometries.Length; i++)
                {
                    ref Mesh mesh = ref meshes[i];

                    GraphicsBuffer meshIndexBuffer = mesh.GetIndexBuffer();
                    GraphicsBuffer meshVertexBuffer = mesh.GetVertexBuffer(0);

                    copyVerticesCS.SetBuffer(0, "InputVertexBuffer", meshVertexBuffer);
                    copyVerticesCS.SetInt("InputVertexStride", meshVertexBuffer.stride);
                    copyVerticesCS.SetInt("InputVertexCount", meshVertexBuffer.count);

                    copyVerticesCS.SetBuffer(0, "OutputVertexBuffer", vertexBuffer);
                    copyVerticesCS.SetInt("OutputVertexBufferOffset", baseVertex * meshVertexBuffer.stride);

                    int threadGroupsX = (meshVertexBuffer.count + 64 - 1) / 64;

                    copyVerticesCS.Dispatch(0, threadGroupsX, 1, 1);

                    baseVertex += meshVertexBuffer.count;

                    meshIndexBuffer.Release();
                    meshVertexBuffer.Release();
                }
            }
        }
        UnityEngine.Random.InitState(12345);

        if (instanceDataBuffer == null)
        {
            instanceDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, instanceData.matrices.Length, Marshal.SizeOf(typeof(RayTracingPerInstanceData)));

            RayTracingPerInstanceData[] perInstanceData = new RayTracingPerInstanceData[instanceData.matrices.Length];

            for (int i = 0; i < instanceData.matrices.Length; i++)
            {
                RayTracingPerInstanceData data = new RayTracingPerInstanceData();

                data.objectToWorld = instanceData.matrices[i];
                data.materialIndex = (uint)UnityEngine.Random.Range(0, materials.Length);
                data.geometryIndex = (uint)UnityEngine.Random.Range(0, meshes.Length);
                data.stuff = 12;
                data.something = 9;

                perInstanceData[i] = data;
            }

            instanceDataBuffer.SetData(perInstanceData);
        }
    }

    void OnDestroy()
    {
        ReleaseResources();
    }

    void OnDisable()
    {
        ReleaseResources();
    }

    private void OnEnable()
    {
        if (rtas != null)
            return;

        rtas = new RayTracingAccelerationStructure();
    }
    private void Update()
    {
        if (fpsText)
        {
            float deltaTime = Time.realtimeSinceStartup - lastRealtimeSinceStartup;
            updateFPSTimer += deltaTime;

            if (updateFPSTimer >= 0.2f)
            {
                float fps = 1.0f / Mathf.Max(deltaTime, 0.0001f);
                fpsText.text = "FPS: " + Mathf.Ceil(fps).ToString();
                updateFPSTimer = 0.0f;
            }

            lastRealtimeSinceStartup = Time.realtimeSinceStartup;
        }
    }

    [ImageEffectOpaque]
    void OnRenderImage(RenderTexture src, RenderTexture dest)
    {
        if (!SystemInfo.supportsRayTracing || !rayTracingShader)
        {
            Debug.Log("The Ray Tracing API is not supported by this GPU or by the current graphics API.");
            Graphics.Blit(src, dest);
            return;
        }

        if (materials.Length == 0)
        {
            Debug.Log("Please setup the Materials!");
            Graphics.Blit(src, dest);
            return;
        }

        CreateResources();

        CommandBuffer cmdBuffer = new CommandBuffer();
        cmdBuffer.name = "Indirect Geometry Instancing Test";

        // Execute GPU culling.
        {
            Vector3 cameraPos = Camera.main.transform.position;
            cmdBuffer.SetBufferCounterValue(instanceIndices, 0);
            cmdBuffer.SetComputeVectorParam(cullingCS, "CameraPosAndRadius2", new Vector4(cameraPos.x, cameraPos.y, cameraPos.z, cullingRadius * cullingRadius));
            cmdBuffer.SetComputeBufferParam(cullingCS, 0, "InstanceData", instanceDataBuffer);
            cmdBuffer.SetComputeBufferParam(cullingCS, 0, "InstanceIndices", instanceIndices);
            cmdBuffer.SetComputeIntParam(cullingCS, "TotalInstanceCount", instanceData.matrices.Length);
            cmdBuffer.SetComputeIntParam(cullingCS, "InstanceDataByteSize", Marshal.SizeOf(typeof(RayTracingPerInstanceData)));
            cmdBuffer.SetComputeIntParam(cullingCS, "ObjectToWorldByteOffset", (int)Marshal.OffsetOf<RayTracingPerInstanceData>("objectToWorld"));

            int threadGroups = (instanceData.matrices.Length + 64 - 1) / 64;
            cmdBuffer.DispatchCompute(cullingCS, 0, threadGroups, 1, 1);

            cmdBuffer.CopyCounterValue(instanceIndices, indirectArgsBuffer, 4);
        }

        // Display RTAS instance count after GPU culling.
        {
            Action<AsyncGPUReadbackRequest> checkOutput = (AsyncGPUReadbackRequest rq) =>
            {
                var count = rq.GetData<uint>();

                if (titleText)
                {
                    titleText.text = "Adding " + count[0] + " instances to RTAS";
                }

            };

            cmdBuffer.RequestAsyncReadback(indirectArgsBuffer, 4, 4, checkOutput);
        }

        rtas.ClearInstances();

        // Add regular MeshRenderers from the scene to the rtas.
        {
            RayTracingInstanceCullingConfig cullingConfig = new RayTracingInstanceCullingConfig();

            cullingConfig.flags = RayTracingInstanceCullingFlags.None;

            RayTracingSubMeshFlagsConfig rayTracingSubMeshFlagsConfig = new RayTracingSubMeshFlagsConfig()
            {
                opaqueMaterials = RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly,
                transparentMaterials = RayTracingSubMeshFlags.Disabled,
                alphaTestedMaterials = RayTracingSubMeshFlags.Disabled
            };

            cullingConfig.subMeshFlagsConfig = rayTracingSubMeshFlagsConfig;

            List<RayTracingInstanceCullingTest> instanceTests = new List<RayTracingInstanceCullingTest>();

            RayTracingInstanceCullingTest instanceTest = new RayTracingInstanceCullingTest()
            {
                allowTransparentMaterials = false,
                allowOpaqueMaterials = true,
                layerMask = 1,
                shadowCastingModeMask = -1,
                instanceMask = 0xff,
            };

            instanceTests.Add(instanceTest);

            cullingConfig.instanceTests = instanceTests.ToArray();

            rtas.CullInstances(ref cullingConfig);
        }

        // Add tree instances to the rtas.
        if (instanceData != null && instancesConfig.subGeometries != null)
        {
            Profiler.BeginSample("Add Ray Tracing Instances to RTAS");

            rtas.AddInstancesIndirect(instancesConfig, instanceDataBuffer, typeof(RayTracingPerInstanceData), instanceIndices, -1, indirectArgsBuffer, 0);

            Profiler.EndSample();
        }

        // Do camera-relative ray tracing. Rays start at (0, 0, 0) in shader code and intersections are in camera space.
        RayTracingAccelerationStructure.BuildSettings buildSettings = new RayTracingAccelerationStructure.BuildSettings()
        {
            buildFlags = RayTracingAccelerationStructureBuildFlags.MinimizeMemory,
            relativeOrigin = Camera.main.transform.position
        };
        cmdBuffer.BuildRayTracingAccelerationStructure(rtas, buildSettings);

        cmdBuffer.SetRayTracingShaderPass(rayTracingShader, "Test");

        // Input
        cmdBuffer.SetRayTracingAccelerationStructure(rayTracingShader, Shader.PropertyToID("g_AccelStruct"), rtas);
        cmdBuffer.SetRayTracingMatrixParam(rayTracingShader, Shader.PropertyToID("g_InvViewMatrix"), Camera.main.cameraToWorldMatrix);
        cmdBuffer.SetRayTracingFloatParam(rayTracingShader, Shader.PropertyToID("g_Zoom"), Mathf.Tan(Mathf.Deg2Rad * Camera.main.fieldOfView * 0.5f));
        cmdBuffer.SetGlobalTexture(Shader.PropertyToID("g_EnvTexture"), envTexture);

        // Output
        cmdBuffer.SetRayTracingTextureParam(rayTracingShader, Shader.PropertyToID("g_Output"), rayTracingOutput);

        cmdBuffer.DispatchRays(rayTracingShader, "MainRayGenShader", cameraWidth, cameraHeight, 1);

        Graphics.ExecuteCommandBuffer(cmdBuffer);

        cmdBuffer.Release();

        Graphics.Blit(rayTracingOutput, dest);
    }
}
