using UnityEditor;
using UnityEditor.Splines;
using UnityEngine.Splines;

namespace SWGenerator{

	[CustomEditor(typeof(SplineWallGeneratorFree))]
	public class SplineWallGeneratorEditor : Editor
	{
		SplineWallGeneratorFree wall;
		SplineContainer ownContainer;

		/*------------------------------------------------------------
		エディタ有効時にスプライン変更イベントを登録する
		------------------------------------------------------------*/
		void OnEnable()
		{
			wall = target as SplineWallGeneratorFree;
			if(wall != null) wall.TryGetComponent(out ownContainer);

			Spline.Changed += OnSplineChanged;
			EditorSplineUtility.AfterSplineWasModified += OnSplineModified;
			SplineContainer.SplineAdded += OnContainerSplineChanged;
			SplineContainer.SplineRemoved += OnContainerSplineChanged;
		}

		/*------------------------------------------------------------
		エディタ無効時にスプライン変更イベントを解除する
		------------------------------------------------------------*/
		void OnDisable()
		{
			Spline.Changed -= OnSplineChanged;
			EditorSplineUtility.AfterSplineWasModified -= OnSplineModified;
			SplineContainer.SplineAdded -= OnContainerSplineChanged;
			SplineContainer.SplineRemoved -= OnContainerSplineChanged;

			wall = null;
			ownContainer = null;
		}

		/*------------------------------------------------------------
		インスペクタGUI描画：パラメータ変更時にメッシュを再構築する
		------------------------------------------------------------*/
		public override void OnInspectorGUI()
		{
			EditorGUI.BeginChangeCheck();
			base.OnInspectorGUI();

			if(EditorGUI.EndChangeCheck())
			{
				//パラメータが変更されたらメッシュを再構築
				if(wall != null)
				{
					wall.Rebuild();
				}
			}
		}

		/*------------------------------------------------------------
		変更されたスプラインが自分のSplineContainerに属するか判定する
		------------------------------------------------------------*/
		bool IsOwnSpline(Spline spline)
		{
			if(ownContainer == null) return false;

			for(int i = 0; i < ownContainer.Splines.Count; ++i)
			{
				if(ownContainer.Splines[i] == spline) return true;
			}

			return false;
		}

		/*------------------------------------------------------------
		Spline.Changedイベントハンドラ：自分のスプラインのみ反応
		------------------------------------------------------------*/
		void OnSplineChanged(Spline spline, int knotIndex, SplineModification modificationType)
		{
			if(IsOwnSpline(spline)) RebuildIfNeeded();
		}

		/*------------------------------------------------------------
		EditorSplineUtility.AfterSplineWasModifiedイベントハンドラ：自分のスプラインのみ反応
		------------------------------------------------------------*/
		void OnSplineModified(Spline spline)
		{
			if(IsOwnSpline(spline)) RebuildIfNeeded();
		}

		/*------------------------------------------------------------
		SplineContainer.SplineAdded/Removedイベントハンドラ：自分のコンテナのみ反応
		------------------------------------------------------------*/
		void OnContainerSplineChanged(SplineContainer container, int spline)
		{
			if(container == ownContainer) RebuildIfNeeded();
		}

		/*------------------------------------------------------------
		プレイモード中でなければメッシュを再構築する
		------------------------------------------------------------*/
		void RebuildIfNeeded()
		{
			if(EditorApplication.isPlayingOrWillChangePlaymode) return;

			if(wall != null)
			{
				wall.Rebuild();
			}
		}
	}

}
