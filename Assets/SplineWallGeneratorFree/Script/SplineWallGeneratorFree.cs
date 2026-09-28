using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;
using static UnityEngine.Rendering.MeshUpdateFlags;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SWGenerator{

	[RequireComponent(typeof(SplineContainer), typeof(MeshFilter), typeof(MeshRenderer))]
	public class SplineWallGeneratorFree : MonoBehaviour
	{
		//頂点データ構造体：位置・法線・UVをシーケンシャルに配置
		[StructLayout(LayoutKind.Sequential)]
		struct VertexData
		{
			public float3 position;
			public float3 normal;
			public float2 uv;
		}

		[SerializeField] SplineContainer splineContainer;

		[SerializeField, Range(2, 200)] int divisions = 100;//分割数
		[SerializeField] float height = 5.0f;//高さ
		[SerializeField] bool flipNormals = false;//法線の向きを反転する：メッシュコライダーの判定方向にも影響

		[SerializeField] bool enableThickness = false;//厚みのある壁メッシュを生成
		[SerializeField, Range(0.01f, 10.0f)] float wallThickness = 0.5f;//壁の厚さ

		[SerializeField] float uvTilingX = 1.0f;//スプライン方向のUVタイリング：ワールド距離1mあたりの繰り返し回数
		[SerializeField] float uvTilingY = 1.0f;//高さ方向のUVタイリング：ワールド距離1mあたりの繰り返し回数

		Mesh mesh;
		MeshFilter meshFilter;

		/*------------------------------------------------------------
		コンポーネントがリセットされた時の初期化処理
		------------------------------------------------------------*/
		void Reset()
		{
			EnsureInitialized();
			Rebuild();

			//マテリアルの自動割り当て：ピンク状態を防止
			#if UNITY_EDITOR
			MeshRenderer meshRenderer = GetComponent<MeshRenderer>();
			if(meshRenderer != null && meshRenderer.sharedMaterial == null)
			{
				//現在のレンダーパイプラインのデフォルトマテリアルを割り当て
				Material defaultMat = GraphicsSettings.currentRenderPipeline != null
					? GraphicsSettings.currentRenderPipeline.defaultMaterial//URP/HDRPのデフォルトマテリアル
					: AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");//Built-inのデフォルトマテリアル
				if(defaultMat != null) meshRenderer.sharedMaterial = defaultMat;

				//デフォルトマテリアルではなく最初から壁のマテリアルを割り当てる：スクリプトのパスからMaterialフォルダのBrick.matを検索して割り当て
				// string scriptPath = AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(this));
				// string assetFolder = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(scriptPath));
				// string materialPath = assetFolder + "/Material/Brick.mat";
				// Material brickMat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
				// if(brickMat != null) meshRenderer.sharedMaterial = brickMat;
			}
			#endif

			//MeshColliderの自動追加：壁の当たり判定用
			MeshCollider meshCollider = GetComponent<MeshCollider>();
			if(meshCollider == null) meshCollider = gameObject.AddComponent<MeshCollider>();
			if(mesh != null) meshCollider.sharedMesh = mesh;
		}

		/*------------------------------------------------------------
		コンポーネントとメッシュの初期化を保証する
		------------------------------------------------------------*/
		void EnsureInitialized()
		{
			if(splineContainer == null) TryGetComponent(out splineContainer);
			if(meshFilter == null) TryGetComponent(out meshFilter);

			//メッシュが未作成の場合のみ新規生成
			if(mesh == null)
			{
				mesh = new Mesh { name = "SplineWall" };
			}

			if(meshFilter.sharedMesh != mesh)
			{
				meshFilter.sharedMesh = mesh;
			}
		}

		/*------------------------------------------------------------
		メッシュを再構築する：外部およびエディタから呼び出し可能
		------------------------------------------------------------*/
		public void Rebuild()
		{
			//コンポーネント未初期化時のフォールバック
			if(splineContainer == null || meshFilter == null || mesh == null)
			{
				EnsureInitialized();
			}

			//全スプラインから有効なもの（ノット2個以上）を収集
			System.Collections.Generic.List<Spline> validSplines = new System.Collections.Generic.List<Spline>();
			foreach(Spline s in splineContainer.Splines)
			{
				if(s != null && s.Count >= 2) validSplines.Add(s);
			}
			if(validSplines.Count == 0) return;

			mesh.Clear();

			//厚みモードに応じてメッシュ生成を分岐
			if(enableThickness)
			{
				BuildThickWall(validSplines);
			}
			else
			{
				BuildFlatWall(validSplines);
			}

			mesh.RecalculateBounds();

			//MeshColliderのメッシュを更新：パラメータ変更時にコライダー形状を追従させる
			MeshCollider meshCollider = GetComponent<MeshCollider>();
			if(meshCollider != null)
			{
				meshCollider.sharedMesh = null;
				meshCollider.sharedMesh = mesh;
			}
		}

		/*------------------------------------------------------------
		スプライン上の各分割点までの累積距離を計算する：UV生成用
		------------------------------------------------------------*/
		float[] CalculateCumulativeDistances(Spline spline)
		{
			float[] distances = new float[divisions + 1];
			distances[0] = 0f;

			spline.Evaluate(0f, out float3 prevPos, out _, out _);

			for(int i = 1; i <= divisions; ++i)
			{
				float t = (float)i / divisions;
				spline.Evaluate(t, out float3 pos, out _, out _);
				distances[i] = distances[i - 1] + math.length(pos - prevPos);
				prevPos = pos;
			}

			return distances;
		}

		/*------------------------------------------------------------
		スプラインの接線から壁の法線方向を計算する
		------------------------------------------------------------*/
		float3 CalculateWallNormal(float3 tangent)
		{
			//接線と上方向のクロス積で壁面に垂直な方向を求める
			float3 cross = math.cross(new float3(0, 1, 0), tangent);
			float lengthSq = math.lengthsq(cross);

			//接線がゼロまたは上方向と平行な場合はフォールバック軸を使用
			if(lengthSq < 1e-8f)
			{
				cross = math.cross(new float3(0, 0, 1), tangent);
				lengthSq = math.lengthsq(cross);

				//それでもゼロならデフォルト方向を返す
				if(lengthSq < 1e-8f)
				{
					return flipNormals ? new float3(-1, 0, 0) : new float3(1, 0, 0);
				}
			}

			float3 normalDir = cross * math.rsqrt(lengthSq);
			return flipNormals ? -normalDir : normalDir;
		}

		/*------------------------------------------------------------
		頂点バッファのレイアウト定義を取得する：位置・法線・UV
		------------------------------------------------------------*/
		VertexAttributeDescriptor[] GetVertexAttributes()
		{
			return new VertexAttributeDescriptor[]
			{
				new(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
				new(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
				new(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
			};
		}

		/*------------------------------------------------------------
		片面の壁メッシュを生成する：Quadのような薄い壁（複数スプライン対応）
		------------------------------------------------------------*/
		void BuildFlatWall(System.Collections.Generic.List<Spline> splines)
		{
			int splineCount = splines.Count;
			int vertsPerSpline = 2 * (divisions + 1);
			int indicesPerSpline = 6 * divisions;
			int totalVertexCount = vertsPerSpline * splineCount;
			int totalIndexCount = indicesPerSpline * splineCount;

			Mesh.MeshDataArray meshDataArray = Mesh.AllocateWritableMeshData(1);
			Mesh.MeshData meshData = meshDataArray[0];
			meshData.subMeshCount = 1;

			meshData.SetVertexBufferParams(totalVertexCount, GetVertexAttributes());
			//頂点数が65535を超える場合はUInt32を使用
			IndexFormat indexFormat = totalVertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
			meshData.SetIndexBufferParams(totalIndexCount, indexFormat);

			NativeArray<VertexData> vertices = meshData.GetVertexData<VertexData>();

			//各スプラインの壁メッシュデータを書き込む
			for(int s = 0; s < splineCount; ++s)
			{
				Spline spline = splines[s];
				int vertOffset = vertsPerSpline * s;
				int idxOffset = indicesPerSpline * s;

				float[] distances = CalculateCumulativeDistances(spline);

				//各分割点の頂点データを設定：下端と上端の2頂点ずつ
				for(int i = 0; i <= divisions; ++i)
				{
					float t = (float)i / divisions;
					spline.Evaluate(t, out float3 position, out float3 tangent, out _);

					float3 normalDir = CalculateWallNormal(tangent);
					float u = distances[i] * uvTilingX;

					vertices[vertOffset + 2 * i] = new VertexData
					{
						position = position,
						normal = normalDir,
						uv = new float2(u, 0)
					};

					vertices[vertOffset + 2 * i + 1] = new VertexData
					{
						position = position + new float3(0, height, 0),
						normal = normalDir,
						uv = new float2(u, uvTilingY)
					};
				}

				//三角形インデックスを設定：flipNormals時は巻き順を反転
				WriteFlatIndices(meshData, indexFormat, idxOffset, vertOffset);
			}

			meshData.SetSubMesh(0, new SubMeshDescriptor(0, totalIndexCount), DontRecalculateBounds | DontValidateIndices);
			Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, mesh);
		}

		/*------------------------------------------------------------
		片面壁の三角形インデックスを書き込む：UInt16/UInt32両対応
		------------------------------------------------------------*/
		void WriteFlatIndices(Mesh.MeshData meshData, IndexFormat format, int idxOffset, int vertOffset)
		{
			for(int i = 0; i < divisions; ++i)
			{
				int baseIdx = idxOffset + 6 * i;
				int bl = vertOffset + 2 * i;       //左下
				int tl = vertOffset + 2 * i + 1;   //左上
				int br = vertOffset + 2 * i + 2;   //右下
				int tr = vertOffset + 2 * i + 3;   //右上

				if(format == IndexFormat.UInt32)
				{
					NativeArray<UInt32> indices = meshData.GetIndexData<UInt32>();
					if(flipNormals)
					{
						indices[baseIdx + 0] = (UInt32)bl; indices[baseIdx + 1] = (UInt32)br; indices[baseIdx + 2] = (UInt32)tl;
						indices[baseIdx + 3] = (UInt32)tl; indices[baseIdx + 4] = (UInt32)br; indices[baseIdx + 5] = (UInt32)tr;
					}
					else
					{
						indices[baseIdx + 0] = (UInt32)bl; indices[baseIdx + 1] = (UInt32)tl; indices[baseIdx + 2] = (UInt32)br;
						indices[baseIdx + 3] = (UInt32)tl; indices[baseIdx + 4] = (UInt32)tr; indices[baseIdx + 5] = (UInt32)br;
					}
				}
				else
				{
					NativeArray<UInt16> indices = meshData.GetIndexData<UInt16>();
					if(flipNormals)
					{
						indices[baseIdx + 0] = (UInt16)bl; indices[baseIdx + 1] = (UInt16)br; indices[baseIdx + 2] = (UInt16)tl;
						indices[baseIdx + 3] = (UInt16)tl; indices[baseIdx + 4] = (UInt16)br; indices[baseIdx + 5] = (UInt16)tr;
					}
					else
					{
						indices[baseIdx + 0] = (UInt16)bl; indices[baseIdx + 1] = (UInt16)tl; indices[baseIdx + 2] = (UInt16)br;
						indices[baseIdx + 3] = (UInt16)tl; indices[baseIdx + 4] = (UInt16)tr; indices[baseIdx + 5] = (UInt16)br;
					}
				}
			}
		}

		/*------------------------------------------------------------
		厚み付きの壁メッシュを生成する：マイター接合で角の食い込みを防止（複数スプライン対応）
		------------------------------------------------------------*/
		void BuildThickWall(System.Collections.Generic.List<Spline> splines)
		{
			int splineCount = splines.Count;
			int pointCount = divisions + 1;

			//全スプラインの合計頂点・インデックス数を計算
			int totalVertexCount = 0;
			int totalIndexCount = 0;
			for(int s = 0; s < splineCount; ++s)
			{
				bool closed = splines[s].Closed;
				totalVertexCount += 8 * pointCount + (closed ? 0 : 8);
				totalIndexCount += 24 * divisions + (closed ? 0 : 12);
			}

			Mesh.MeshDataArray meshDataArray = Mesh.AllocateWritableMeshData(1);
			Mesh.MeshData meshData = meshDataArray[0];
			meshData.subMeshCount = 1;

			meshData.SetVertexBufferParams(totalVertexCount, GetVertexAttributes());
			IndexFormat indexFormat = totalVertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
			meshData.SetIndexBufferParams(totalIndexCount, indexFormat);

			NativeArray<VertexData> vertices = meshData.GetVertexData<VertexData>();

			float halfThickness = wallThickness * 0.5f;
			float3 heightVec = new float3(0, height, 0);

			int vertCursor = 0;
			int idxCursor = 0;

			//各スプラインの厚み壁データを書き込む
			for(int s = 0; s < splineCount; ++s)
			{
				Spline spline = splines[s];
				bool isClosed = spline.Closed;
				int splineVertCount = 8 * pointCount + (isClosed ? 0 : 8);
				int splineIdxCount = 24 * divisions + (isClosed ? 0 : 12);

				float[] distances = CalculateCumulativeDistances(spline);

				//各分割点の位置を事前計算
				float3[] positions = new float3[pointCount];
				for(int i = 0; i < pointCount; ++i)
				{
					float t = (float)i / divisions;
					spline.Evaluate(t, out positions[i], out _, out _);
				}

				//各セグメントのエッジ法線を計算
				float3[] edgeNormals = new float3[divisions];
				for(int i = 0; i < divisions; ++i)
				{
					float3 edgeDir = positions[i + 1] - positions[i];
					float edgeLenSq = math.lengthsq(edgeDir);
					if(edgeLenSq < 1e-12f)
					{
						edgeNormals[i] = (i > 0) ? edgeNormals[i - 1] : CalculateWallNormal(new float3(1, 0, 0));
					}
					else
					{
						edgeNormals[i] = CalculateWallNormal(edgeDir * math.rsqrt(edgeLenSq));
					}
				}

				//各頂点のマイター法線とオフセットスケールを計算
				float3[] miterNormals = new float3[pointCount];
				float[] miterScales = new float[pointCount];

				for(int i = 0; i < pointCount; ++i)
				{
					if(i == 0 && !isClosed)
					{
						miterNormals[i] = edgeNormals[0];
						miterScales[i] = 1f;
					}
					else if(i == divisions && !isClosed)
					{
						miterNormals[i] = edgeNormals[divisions - 1];
						miterScales[i] = 1f;
					}
					else
					{
						int inEdge = (i == 0) ? divisions - 1 : i - 1;
						int outEdge = (i >= divisions) ? 0 : i;

						float3 n1 = edgeNormals[inEdge];
						float3 n2 = edgeNormals[outEdge];
						float3 miterSum = n1 + n2;
						float miterLenSq = math.lengthsq(miterSum);

						if(miterLenSq < 1e-8f)
						{
							miterNormals[i] = n2;
							miterScales[i] = 1f;
						}
						else
						{
							float3 miterDir = math.normalize(miterSum);
							float cosHalfAngle = math.max(math.dot(miterDir, n1), 0.2f);
							miterNormals[i] = miterDir;
							miterScales[i] = 1f / cosHalfAngle;
						}
					}
				}

				//各分割点の8頂点を設定：マイター法線で位置を補正
				for(int i = 0; i < pointCount; ++i)
				{
					float3 normalDir = miterNormals[i];
					float adjustedHalf = halfThickness * miterScales[i];
					float u = distances[i] * uvTilingX;

					float3 frontOffset = normalDir * adjustedHalf;
					float3 backOffset = -normalDir * adjustedHalf;
					float3 pos = positions[i];

					int baseVertex = vertCursor + 8 * i;

					vertices[baseVertex + 0] = new VertexData { position = pos + frontOffset, normal = normalDir, uv = new float2(u, 0) };
					vertices[baseVertex + 1] = new VertexData { position = pos + frontOffset + heightVec, normal = normalDir, uv = new float2(u, uvTilingY) };
					vertices[baseVertex + 2] = new VertexData { position = pos + backOffset, normal = -normalDir, uv = new float2(u, 0) };
					vertices[baseVertex + 3] = new VertexData { position = pos + backOffset + heightVec, normal = -normalDir, uv = new float2(u, uvTilingY) };
					vertices[baseVertex + 4] = new VertexData { position = pos + frontOffset + heightVec, normal = new float3(0, 1, 0), uv = new float2(u, 0) };
					vertices[baseVertex + 5] = new VertexData { position = pos + backOffset + heightVec, normal = new float3(0, 1, 0), uv = new float2(u, wallThickness * uvTilingX) };
					vertices[baseVertex + 6] = new VertexData { position = pos + frontOffset, normal = new float3(0, -1, 0), uv = new float2(u, 0) };
					vertices[baseVertex + 7] = new VertexData { position = pos + backOffset, normal = new float3(0, -1, 0), uv = new float2(u, wallThickness * uvTilingX) };
				}

				//各セグメントの4面の三角形インデックスを設定
				for(int i = 0; i < divisions; ++i)
				{
					int curr = vertCursor + 8 * i;
					int next = vertCursor + 8 * (i + 1);
					WriteThickSegmentIndices(meshData, indexFormat, ref idxCursor, curr, next);
				}

				//端面キャップ：スプラインが閉じていない場合のみ生成
				if(!isClosed)
				{
					int capBase = vertCursor + 8 * pointCount;

					float3 startEdgeDir = math.normalize(positions[1] - positions[0]);
					BuildCapVertices(-startEdgeDir, miterNormals[0], positions[0], halfThickness, heightVec, capBase, vertices);
					WriteCapIndices(meshData, indexFormat, capBase, true, ref idxCursor);

					float3 endEdgeDir = math.normalize(positions[divisions] - positions[divisions - 1]);
					BuildCapVertices(endEdgeDir, miterNormals[divisions], positions[divisions], halfThickness, heightVec, capBase + 4, vertices);
					WriteCapIndices(meshData, indexFormat, capBase + 4, false, ref idxCursor);
				}

				vertCursor += splineVertCount;
			}

			meshData.SetSubMesh(0, new SubMeshDescriptor(0, totalIndexCount), DontRecalculateBounds | DontValidateIndices);
			Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, mesh);
		}

		/*------------------------------------------------------------
		厚み壁の1セグメント分（4面）のインデックスを書き込む：UInt16/UInt32両対応
		------------------------------------------------------------*/
		void WriteThickSegmentIndices(Mesh.MeshData meshData, IndexFormat format, ref int idx, int curr, int next)
		{
			if(format == IndexFormat.UInt32)
			{
				NativeArray<UInt32> indices = meshData.GetIndexData<UInt32>();
				//表面
				indices[idx++] = (UInt32)(curr + 0); indices[idx++] = (UInt32)(curr + 1); indices[idx++] = (UInt32)(next + 0);
				indices[idx++] = (UInt32)(curr + 1); indices[idx++] = (UInt32)(next + 1); indices[idx++] = (UInt32)(next + 0);
				//裏面
				indices[idx++] = (UInt32)(curr + 2); indices[idx++] = (UInt32)(next + 2); indices[idx++] = (UInt32)(curr + 3);
				indices[idx++] = (UInt32)(curr + 3); indices[idx++] = (UInt32)(next + 2); indices[idx++] = (UInt32)(next + 3);
				//上面
				indices[idx++] = (UInt32)(curr + 4); indices[idx++] = (UInt32)(curr + 5); indices[idx++] = (UInt32)(next + 4);
				indices[idx++] = (UInt32)(curr + 5); indices[idx++] = (UInt32)(next + 5); indices[idx++] = (UInt32)(next + 4);
				//下面
				indices[idx++] = (UInt32)(curr + 6); indices[idx++] = (UInt32)(next + 6); indices[idx++] = (UInt32)(curr + 7);
				indices[idx++] = (UInt32)(curr + 7); indices[idx++] = (UInt32)(next + 6); indices[idx++] = (UInt32)(next + 7);
			}
			else
			{
				NativeArray<UInt16> indices = meshData.GetIndexData<UInt16>();
				indices[idx++] = (UInt16)(curr + 0); indices[idx++] = (UInt16)(curr + 1); indices[idx++] = (UInt16)(next + 0);
				indices[idx++] = (UInt16)(curr + 1); indices[idx++] = (UInt16)(next + 1); indices[idx++] = (UInt16)(next + 0);
				indices[idx++] = (UInt16)(curr + 2); indices[idx++] = (UInt16)(next + 2); indices[idx++] = (UInt16)(curr + 3);
				indices[idx++] = (UInt16)(curr + 3); indices[idx++] = (UInt16)(next + 2); indices[idx++] = (UInt16)(next + 3);
				indices[idx++] = (UInt16)(curr + 4); indices[idx++] = (UInt16)(curr + 5); indices[idx++] = (UInt16)(next + 4);
				indices[idx++] = (UInt16)(curr + 5); indices[idx++] = (UInt16)(next + 5); indices[idx++] = (UInt16)(next + 4);
				indices[idx++] = (UInt16)(curr + 6); indices[idx++] = (UInt16)(next + 6); indices[idx++] = (UInt16)(curr + 7);
				indices[idx++] = (UInt16)(curr + 7); indices[idx++] = (UInt16)(next + 6); indices[idx++] = (UInt16)(next + 7);
			}
		}

		/*------------------------------------------------------------
		端面キャップのインデックスを書き込む：UInt16/UInt32両対応
		------------------------------------------------------------*/
		void WriteCapIndices(Mesh.MeshData meshData, IndexFormat format, int vertBase, bool isStart, ref int idx)
		{
			if(format == IndexFormat.UInt32)
			{
				NativeArray<UInt32> indices = meshData.GetIndexData<UInt32>();
				if(isStart)
				{
					indices[idx++] = (UInt32)(vertBase + 0); indices[idx++] = (UInt32)(vertBase + 2); indices[idx++] = (UInt32)(vertBase + 1);
					indices[idx++] = (UInt32)(vertBase + 0); indices[idx++] = (UInt32)(vertBase + 3); indices[idx++] = (UInt32)(vertBase + 2);
				}
				else
				{
					indices[idx++] = (UInt32)(vertBase + 0); indices[idx++] = (UInt32)(vertBase + 1); indices[idx++] = (UInt32)(vertBase + 2);
					indices[idx++] = (UInt32)(vertBase + 0); indices[idx++] = (UInt32)(vertBase + 2); indices[idx++] = (UInt32)(vertBase + 3);
				}
			}
			else
			{
				NativeArray<UInt16> indices = meshData.GetIndexData<UInt16>();
				if(isStart)
				{
					indices[idx++] = (UInt16)(vertBase + 0); indices[idx++] = (UInt16)(vertBase + 2); indices[idx++] = (UInt16)(vertBase + 1);
					indices[idx++] = (UInt16)(vertBase + 0); indices[idx++] = (UInt16)(vertBase + 3); indices[idx++] = (UInt16)(vertBase + 2);
				}
				else
				{
					indices[idx++] = (UInt16)(vertBase + 0); indices[idx++] = (UInt16)(vertBase + 1); indices[idx++] = (UInt16)(vertBase + 2);
					indices[idx++] = (UInt16)(vertBase + 0); indices[idx++] = (UInt16)(vertBase + 2); indices[idx++] = (UInt16)(vertBase + 3);
				}
			}
		}

		/*------------------------------------------------------------
		端面キャップの4頂点を生成する
		------------------------------------------------------------*/
		void BuildCapVertices(float3 capNormal, float3 wallNormal, float3 position,
			float halfThickness, float3 heightVec, int vertBase, NativeArray<VertexData> vertices)
		{
			float3 frontPos = position + wallNormal * halfThickness;
			float3 backPos = position - wallNormal * halfThickness;

			vertices[vertBase + 0] = new VertexData { position = frontPos, normal = capNormal, uv = new float2(0, 0) };
			vertices[vertBase + 1] = new VertexData { position = frontPos + heightVec, normal = capNormal, uv = new float2(0, uvTilingY) };
			vertices[vertBase + 2] = new VertexData { position = backPos + heightVec, normal = capNormal, uv = new float2(wallThickness * uvTilingX, uvTilingY) };
			vertices[vertBase + 3] = new VertexData { position = backPos, normal = capNormal, uv = new float2(wallThickness * uvTilingX, 0) };
		}

	}

}
