# AddInstancesIndirect<T>
Test project that uses `RayTracingAccelerationStructure.AddInstancesIndirect<T>` to add many ray tracing instances to an acceleration structure in a single call.

Function signatures:

```
int RayTracingAccelerationStructure.AddInstancesIndirect<T>(RayTracingMultiGeometryInstanceConfig config, GraphicsBuffer instanceData, GraphicsBuffer instanceIndices, int maxInstanceCount, GraphicsBuffer argsBuffer, uint argsOffset, uint id)
int RayTracingAccelerationStructure.AddInstancesIndirect(RayTracingMultiGeometryInstanceConfig config, GraphicsBuffer instanceData, Type instanceType, GraphicsBuffer instanceIndices, int maxInstanceCount, GraphicsBuffer argsBuffer, uint argsOffset, uint id)
```

The user can specify custom instance data using the instanceData `GraphicsBuffer` that stores per instance data. The instance data format is described by passing a simple structure as generic type template argument `<T>`. This structure can have any format and fields but it must contain the following mandatory fields: objectToWorld(Matrix4x4), geometryIndex(uint) and materialIndex(uint). The materials and geometries are configured using the method argument `RayTracingMultiGeometryInstanceConfig config`. The sample performs a GPU culling step in a compute shader outputing a buffer of instance indices that are inside a radius around the camera. The final amount of valid ray tracing instances must be written into the indirect arguments GraphicsBuffer which contains 2 integers at an offset: start instance, instance count. In this test, these values are 0 and the amount of instances that passed the culling test.

# Requirements
Unity 6.3 beta1 +
