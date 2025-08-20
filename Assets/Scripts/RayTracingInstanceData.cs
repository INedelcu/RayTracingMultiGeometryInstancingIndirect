using UnityEngine;
using Unity.Collections;
using System;

sealed class RayTracingInstanceData : IDisposable
{
    public int rows;
    public int columns;

    public NativeArray<Matrix4x4> matrices;

    public RayTracingInstanceData(int _columns, int _rows)
    {
        rows = _rows;
        columns = _columns;

        matrices = new NativeArray<Matrix4x4>(rows * columns, Allocator.Persistent);

        int index = 0;

        NativeArray<Vector3> data = new NativeArray<Vector3>(rows * columns, Allocator.Temp);

        Matrix4x4 m = Matrix4x4.identity;

        UnityEngine.Random.InitState(12345);

        float angle = 0;

        for (int row = 0; row < rows; row++)
        {
            float z = row + 0.5f - rows * 0.5f;

            for (int column = 0; column < columns; column++)
            {
                float x = column + 0.5f - columns * 0.5f;

                angle += 10.0f;

                Quaternion rotation = Quaternion.Euler(UnityEngine.Random.Range(-4, 4), angle, UnityEngine.Random.Range(-4, 4));

                Vector3 position = new Vector3(30 * x, 0, 30 * z);

                position += new Vector3(UnityEngine.Random.Range(-10, 10), 0, UnityEngine.Random.Range(-10, 10));

                Vector3 scale = Vector3.one * 10;

                scale += UnityEngine.Random.Range(-2, 2) * new Vector3(1, 0, 1);
                scale += new Vector3(0, UnityEngine.Random.Range(-1, 1), 0);

                m.SetTRS(position, rotation, scale);

                matrices[index] = m;

                index++;
            }
        }
    }

    public void Dispose()
    {
        if (matrices.IsCreated)
        {
            matrices.Dispose();
        }
    }
}