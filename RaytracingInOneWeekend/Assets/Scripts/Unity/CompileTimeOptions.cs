using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEngine;
#if ODIN_INSPECTOR

#else
using OdinMock;
#endif

namespace Unity
{
	[ExecuteInEditMode]
	class CompileTimeOptions : MonoBehaviour
	{
		[SerializeField] [DisableInPlayMode]
		bool fullDiagnostics = false, pathDebugging = false, enableNvidiaOptix = false, trace = false, profiling = false;

#if UNITY_EDITOR
		void OnValidate()
		{
			if (Application.isPlaying || UnityEditor.EditorApplication.isCompiling)
				return;

			string currentDefines =
				UnityEditor.PlayerSettings.GetScriptingDefineSymbolsForGroup(UnityEditor.BuildTargetGroup.Standalone);

			var originalDefinitions = new HashSet<string>(currentDefines.Split(';'));
			var newDefinitions = new HashSet<string>(originalDefinitions);

			newDefinitions.Remove("FULL_DIAGNOSTICS");
			newDefinitions.Remove("PATH_DEBUGGING");
			newDefinitions.Remove("ENABLE_OPTIX");
			newDefinitions.Remove("TRACE_LOGGING");
			newDefinitions.Remove("PROFILING");

			if (fullDiagnostics) newDefinitions.Add("FULL_DIAGNOSTICS");
			if (pathDebugging) newDefinitions.Add("PATH_DEBUGGING");
			if (enableNvidiaOptix) newDefinitions.Add("ENABLE_OPTIX");
			if (trace) newDefinitions.Add("TRACE_LOGGING");
			if (profiling) newDefinitions.Add("PROFILING");

			if (!newDefinitions.SetEquals(originalDefinitions))
			{
				UnityEditor.EditorApplication.delayCall += () =>
					UnityEditor.PlayerSettings.SetScriptingDefineSymbolsForGroup(
						UnityEditor.BuildTargetGroup.Standalone,
						string.Join(";", newDefinitions));
			}
		}
#endif
	}
}