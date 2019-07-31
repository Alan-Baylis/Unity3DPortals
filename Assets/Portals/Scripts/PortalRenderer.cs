// -----------------------------------------------------------------------------------------------------------
// <summary>
// Renders a portal using multiple cameras.
// </summary>
// -----------------------------------------------------------------------------------------------------------
namespace Portals {
    using System;
    using System.Collections.Generic;
    using UnityEngine;
    using UnityEngine.VR;
    using UnityEngine.Rendering;

    [ExecuteInEditMode]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class PortalRenderer : RenderedBehaviour {
        #region Members
        // Mesh spawned when walking through a portal so that you can't clip through the portal
        private static Mesh m_Mesh;

        // Counts the number of active PortalRenderers in the scene.
        private static int m_ActivePortalRendererCount = 0;

        private Portal m_Portal;

        // Maps cameras to their children
        private Dictionary<Camera, PortalCamera> m_PortalCamByCam = new Dictionary<Camera, PortalCamera>();

        // Used to track current recursion depth
        private static int s_Depth;

        // Instanced materials
        private Material m_PortalMaterial;
        private Material m_BackfaceMaterial;

        // Members used to save and restore material properties between rendering in the same frame
        private Stack<MaterialPropertyBlock> m_PropertyBlockStack;
        private Stack<ShaderKeyword> m_ShaderKeywordStack;
        private ObjectPool<MaterialPropertyBlock> m_PropertyBlockObjectPool;

        // Cached components
        private Renderer m_Renderer;
        private MeshFilter m_MeshFilter;
        private Transform m_Transform;
        #endregion

        #region Properties
        public static Mesh Mesh {
            get {
                if (!m_Mesh) {
                    m_Mesh = MakePortalMesh();
                }
                return m_Mesh;
            }
        }
        #endregion

        #region Enums
        [Flags]
        private enum ShaderKeyword {
            None = 0,
            SamplePreviousFrame = 1,
            SampleDefaultTexture = 2,
        }
        #endregion

        #region Rendering
        protected override void PostRender() {
            RestoreMaterialProperties();
        }

        private Vector3 ClampedWorldToViewportPoint(Camera cam, Vector3 worldPoint) {
            Vector3 viewportPoint = cam.WorldToViewportPoint(worldPoint);
            //if (viewportPoint.z < 0) {
            //    if (viewportPoint.x < 0.5f) {
            //        viewportPoint.x = 1;
            //    } else {
            //        viewportPoint.x = 0;
            //    }
            //    if (viewportPoint.y < 0.5f) {
            //        viewportPoint.y = 1;
            //    } else {
            //        viewportPoint.y = 0;
            //    }

            //    //if(Vector3.Dot(cam.transform.forward, transform.forward) > 0) {
            //    //    viewportPoint.x = viewportPoint.x - 1;
            //    //    viewportPoint.y = viewportPoint.y - 1;
            //    //}
            //    Debug.Log(Vector3.Dot(cam.transform.forward, transform.forward));
            //}
            return viewportPoint;
        }

        private Vector3 Min(params Vector3[] vecs) {
            Vector3 min = new Vector3(Mathf.Infinity, Mathf.Infinity, Mathf.Infinity);
            foreach (Vector3 vec in vecs) {
                if (vec.z > 0) {
                    min = Vector3.Min(min, vec);
                }
            }
            return min;
        }

        private Vector3 Max(params Vector3[] vecs) {
            Vector3 max = new Vector3(Mathf.NegativeInfinity, Mathf.NegativeInfinity, Mathf.NegativeInfinity);
            foreach (Vector3 vec in vecs) {
                if (vec.z > 0) {
                    max = Vector3.Max(max, vec);
                }
            }
            return max;
        }

        private Rect CalculatePortalViewportRect(Camera cam) {
            var corners = m_Portal.WorldSpaceCorners();

            // TODO: do this shit better. cache worldspacecorners maybe
            Vector3 tl = ClampedWorldToViewportPoint(Camera.current, corners[0]);
            Vector3 tr = ClampedWorldToViewportPoint(Camera.current, corners[1]);
            Vector3 br = ClampedWorldToViewportPoint(Camera.current, corners[2]);
            Vector3 bl = ClampedWorldToViewportPoint(Camera.current, corners[3]);

            Vector3 ftl = ClampedWorldToViewportPoint(Camera.current, m_Portal.transform.TransformPoint(new Vector3(-0.5f, 0.5f, 1.0f)));
            Vector3 ftr = ClampedWorldToViewportPoint(Camera.current, m_Portal.transform.TransformPoint(new Vector3(0.5f, 0.5f, 1.0f)));
            Vector3 fbr = ClampedWorldToViewportPoint(Camera.current, m_Portal.transform.TransformPoint(new Vector3(0.5f, -0.5f, 1.0f)));
            Vector3 fbl = ClampedWorldToViewportPoint(Camera.current, m_Portal.transform.TransformPoint(new Vector3(-0.5f, -0.5f, 1.0f)));
            
            var min = Min(tl, tr, br, bl, ftl, ftr, fbr, fbl);
            var max = Max(tl, tr, br, bl, ftl, ftr, fbr, fbl);

            Rect viewportRect = new Rect(min.x, min.y, max.x - min.x, max.y - min.y);
            return viewportRect;
        }

        protected override void PreRender() {
#if UNITY_EDITOR
            // Workaround for Unity bug that causes Awake/Start to not be called when running in EditMode
            // https://issuetracker.unity3d.com/issues/awake-and-start-not-called-before-update-when-assembly-is-reloaded-for-executeineditmode-scripts
            if (m_PropertyBlockObjectPool == null) {
                Awake();
            }
#endif
            SaveMaterialProperties();

            if (!enabled || !m_Renderer || !m_Renderer.enabled) {
                return;
            }

            if (!m_Portal.ExitPortal || !m_Portal.ExitPortal.isActiveAndEnabled) {
                m_PortalMaterial.EnableKeyword("SAMPLE_DEFAULT_TEXTURE");
                return;
            }

            PortalCamera currentPortalCamera = PortalCamera.current;

            // Don't ever render our own exit portal
            if (s_Depth > 0 && currentPortalCamera != null && m_Portal == currentPortalCamera.portal.ExitPortal) {
                return;
            }

            // The stencil buffer gets used by Unity in deferred rendering and must clear itself, otherwise
            // it will be full of junk. https://docs.unity3d.com/Manual/SL-Stencil.html
            if (s_Depth == 0) {
                if (Camera.current.actualRenderingPath == RenderingPath.DeferredLighting || Camera.current.actualRenderingPath == RenderingPath.DeferredShading) {
                    Camera.current.clearStencilAfterLightingPass = true;
                }
            }

            // Stop recursion when we reach maximum depth
            if (s_Depth >= m_Portal.MaxRecursion) {
                if (m_Portal.FakeInfiniteRecursion && m_Portal.MaxRecursion >= 2) {
                    // Use the previous frame's RenderTexture from the parent camera to render the bottom layer.
                    // This creates an illusion of infinite recursion, but only works with at least two real recursions
                    // because the effect is unconvincing using the Main Camera's previous frame.
                    Camera parentCam = currentPortalCamera.parent;
                    PortalCamera parentPortalCam = parentCam.GetComponent<PortalCamera>();

                    if (currentPortalCamera.portal == m_Portal && parentPortalCam.portal == m_Portal) {
                        // This portal is currently viewing itself.
                        // Render the bottom of the stack with the parent camera's view/projection.
                        PortalCamera.FrameData frameData = parentPortalCam.PreviousFrameData;
                        Matrix4x4 projectionMatrix;
                        Matrix4x4 worldToCameraMatrix;
                        Texture texture;
                        string sampler;
                        switch (parentCam.stereoTargetEye) {
                            case StereoTargetEyeMask.Right:
                                projectionMatrix = frameData.rightEyeProjectionMatrix;
                                worldToCameraMatrix = frameData.rightEyeWorldToCameraMatrix;
                                texture = frameData.rightEyeTexture;
                                sampler = "_RightEyeTexture";
                                break;
                            case StereoTargetEyeMask.Left:
                            case StereoTargetEyeMask.Both:
                            case StereoTargetEyeMask.None:
                            default:
                                projectionMatrix = frameData.leftEyeProjectionMatrix;
                                worldToCameraMatrix = frameData.leftEyeWorldToCameraMatrix;
                                texture = frameData.leftEyeTexture;
                                sampler = "_LeftEyeTexture";
                                break;
                        }

                        m_PortalMaterial.EnableKeyword("SAMPLE_PREVIOUS_FRAME");
                        m_PortalMaterial.SetMatrix("PORTAL_MATRIX_VP", projectionMatrix * worldToCameraMatrix);
                        m_PortalMaterial.SetTexture(sampler, texture);
                    } else {
                        // We are viewing another portal.
                        // Render the bottom of the stack with a base texture
                        m_PortalMaterial.EnableKeyword("SAMPLE_DEFAULT_TEXTURE");
                    }
                } else {
                    m_PortalMaterial.DisableKeyword("SAMPLE_PREVIOUS_FRAME");
                    m_PortalMaterial.EnableKeyword("SAMPLE_DEFAULT_TEXTURE");
                }
                
                // Exit. We don't need to process any further depths.
                return;
            }

            MaterialPropertyBlock block = m_PropertyBlockObjectPool.Take();
            m_Renderer.GetPropertyBlock(block);

            // Handle the player clipping through the portal's frontface
            bool renderBackface = ShouldRenderBackface(Camera.current);
            block.SetFloat("_BackfaceAlpha", renderBackface ? 1.0f : 0.0f);

            // Get camera for next depth level
            PortalCamera portalCamera = GetOrCreatePortalCamera(Camera.current);

            // TODO: Split into separate method
            // Calculate where in screen space the portal lies.
            // We use this to only render as much of the screen as necessary, avoiding overdraw.
            Rect viewportRect = CalculatePortalViewportRect(Camera.current);

            s_Depth++;
            //RenderTexture tex = portalCamera.RenderToTexture2();
            //block.SetTexture("_PortalTexture", tex);
            if (Camera.current.stereoEnabled) {
                // Stereo rendering. Render both eyes.
                if (Camera.current.stereoTargetEye == StereoTargetEyeMask.Both || Camera.current.stereoTargetEye == StereoTargetEyeMask.Left) {
                    RenderTexture tex = portalCamera.RenderToTexture(Camera.MonoOrStereoscopicEye.Left, viewportRect, renderBackface);
                    block.SetTexture("_LeftEyeTexture", tex);
                }
                if (Camera.current.stereoTargetEye == StereoTargetEyeMask.Both || Camera.current.stereoTargetEye == StereoTargetEyeMask.Right) {
                    RenderTexture tex = portalCamera.RenderToTexture(Camera.MonoOrStereoscopicEye.Right, viewportRect, renderBackface);
                    block.SetTexture("_RightEyeTexture", tex);
                }
            } else {
                // Mono rendering. Render only one eye, but set which texture to use based on the camera's target eye.
                RenderTexture tex = portalCamera.RenderToTexture(Camera.MonoOrStereoscopicEye.Mono, viewportRect, renderBackface);
                block.SetTexture("_LeftEyeTexture", tex);
                //switch (Camera.current.stereoTargetEye) {
                //    case StereoTargetEyeMask.Right:
                //        block.SetTexture("_RightEyeTexture", tex);
                //        break;
                //    case StereoTargetEyeMask.None:
                //    case StereoTargetEyeMask.Left:
                //    case StereoTargetEyeMask.Both:
                //    default:
                //        block.SetTexture("_LeftEyeTexture", tex);
                //        break;
                //}
            }
            s_Depth--;
            m_Renderer.SetPropertyBlock(block);
            m_PropertyBlockObjectPool.Give(block);
        }
        #endregion

        #region Initialization
        private void Awake() {
            m_Portal = GetComponentInParent<Portal>();
            m_Renderer = GetComponent<MeshRenderer>();
            m_MeshFilter = GetComponent<MeshFilter>();

            m_Transform = this.transform;
            m_PropertyBlockStack = new Stack<MaterialPropertyBlock>();
            m_ShaderKeywordStack = new Stack<ShaderKeyword>();
            m_PropertyBlockObjectPool = new ObjectPool<MaterialPropertyBlock>(1, () => new MaterialPropertyBlock());

            //// TODO
            //// this.gameObject.layer = PortalPhysics.PortalLayer;

            m_MeshFilter.sharedMesh = PortalRenderer.Mesh;
            if (!m_PortalMaterial || !m_BackfaceMaterial) {
                Material portalMaterial = new Material(Shader.Find("Portal/Portal"));
                Material backFaceMaterial = new Material(Shader.Find("Portal/PortalBackface"));

                m_PortalMaterial = portalMaterial;
                m_BackfaceMaterial = backFaceMaterial;

                m_PortalMaterial.SetTexture("_TransparencyMask", m_Portal.TransparencyMask);
                m_PortalMaterial.SetTexture("_DefaultTexture", m_Portal.DefaultTexture);

                m_Renderer.sharedMaterials = new Material[] {
                    m_PortalMaterial,
                    m_BackfaceMaterial,
                };
            }
        }

        private void OnEnable() {
            m_Portal.OnDefaultTextureChanged += OnDefaultTextureChanged;
            m_Portal.OnTransparencyMaskChanged += OnTransparencyMaskChanged;

            if (m_ActivePortalRendererCount == 0) {
                Camera.onPreRender += SetCurrentEyeGlobal;
                Camera.onPostRender += RestoreCurrentEyeGlobal;
            }
            m_ActivePortalRendererCount += 1;
        }

        private void OnDisable() {
            m_Portal.OnDefaultTextureChanged -= OnDefaultTextureChanged;
            m_Portal.OnTransparencyMaskChanged -= OnTransparencyMaskChanged;

            // Clean up cameras in scene. This is important when using ExecuteInEditMode because
            // script recompilation will disable then enable this script causing creation of duplicate
            // cameras.
            foreach (KeyValuePair<Camera, PortalCamera> kvp in m_PortalCamByCam) {
                PortalCamera child = kvp.Value;
                if (child && child.gameObject) {
                    Util.SafeDestroy(child.gameObject);
                }
            }

            m_ActivePortalRendererCount -= 1;
            if (m_ActivePortalRendererCount == 0) {
                Camera.onPreRender -= SetCurrentEyeGlobal;
                Camera.onPostRender -= RestoreCurrentEyeGlobal;
            }
        }
        #endregion

        ////private void Update() {
        ////    m_Transform.localScale = Vector3.one;
        ////}
        
        #region Callbacks
        private void OnDefaultTextureChanged(Portal portal, Texture oldTexture, Texture newTexture) {
            m_PortalMaterial.SetTexture("_DefaultTexture", newTexture);
        }

        private void OnTransparencyMaskChanged(Portal portal, Texture oldTexture, Texture newTexture) {
            m_PortalMaterial.SetTexture("_TransparencyMask", newTexture);
        }


        private static Stack<float> eyeStack = new Stack<float>();
        private static void SetCurrentEyeGlobal(Camera cam) {
            eyeStack.Push(Shader.GetGlobalFloat("_PortalMultiPassCurrentEye"));
            // Globally set the current eye for Multi-Pass stereo rendering.
            // We also run this code in Single-Pass rendering because Unity doesn't have a runtime
            // check for single/multi-pass stereo, but the value gets ignored.
            Shader.SetGlobalFloat("_PortalMultiPassCurrentEye", (int)cam.stereoActiveEye);
        }

        private static void RestoreCurrentEyeGlobal(Camera cam) {
            float eye = eyeStack.Pop();
            Shader.SetGlobalFloat("_PortalMultiPassCurrentEye", eye);
        }
        #endregion

        #region Private Methods
        private void SaveMaterialProperties() {
            MaterialPropertyBlock block = m_PropertyBlockObjectPool.Take();
            m_Renderer.GetPropertyBlock(block);
            m_PropertyBlockStack.Push(block);

            ShaderKeyword keywords = ShaderKeyword.None;
            if (m_PortalMaterial.IsKeywordEnabled("SAMPLE_PREVIOUS_FRAME")) {
                keywords |= ShaderKeyword.SamplePreviousFrame;
            }
            if (m_PortalMaterial.IsKeywordEnabled("SAMPLE_DEFAULT_TEXTURE")) {
                keywords |= ShaderKeyword.SampleDefaultTexture;
            }
            m_ShaderKeywordStack.Push(keywords);
        }

        private void RestoreMaterialProperties() {
            MaterialPropertyBlock block = m_PropertyBlockStack.Pop();
            m_Renderer.SetPropertyBlock(block);
            m_PropertyBlockObjectPool.Give(block);

            ShaderKeyword keywords = m_ShaderKeywordStack.Pop();
            if ((keywords & ShaderKeyword.SamplePreviousFrame) == ShaderKeyword.SamplePreviousFrame) {
                m_PortalMaterial.EnableKeyword("SAMPLE_PREVIOUS_FRAME");
            } else {
                m_PortalMaterial.DisableKeyword("SAMPLE_PREVIOUS_FRAME");
            }

            if ((keywords & ShaderKeyword.SampleDefaultTexture) == ShaderKeyword.SampleDefaultTexture) {
                m_PortalMaterial.EnableKeyword("SAMPLE_DEFAULT_TEXTURE");
            } else {
                m_PortalMaterial.DisableKeyword("SAMPLE_DEFAULT_TEXTURE");
            }
        }

        private bool ShouldRenderBackface(Camera camera) {
            // Decrease the size of the box in which we will render the backface when rendering stereo.
            // This will prevent the backface from appearing in one eye when near the edge of the portal.
            float scaleMultiplier = camera.stereoEnabled ? 0.9f : 1.0f;
            if (s_Depth == 0 && LocalXYPlaneContainsPoint(Camera.current.transform.position, scaleMultiplier)) {
                // Camera is within the border of the camera
                if (!m_Portal.Plane.GetSide(Camera.current.transform.position)) {
                    return true;
                }
            }
            return false;
        }
        
        private bool LocalXYPlaneContainsPoint(Vector3 point, float scaleMultiplier) {
            float extents = 0.5f * scaleMultiplier;
            Vector3 localPoint = m_Transform.InverseTransformPoint(point);
            if (localPoint.x < -extents) return false;
            if (localPoint.x > extents) return false;
            if (localPoint.y > extents) return false;
            if (localPoint.y < -extents) return false;
            return true;
        }

        private PortalCamera GetOrCreatePortalCamera(Camera currentCamera) {
            PortalCamera portalCamera = null;
            m_PortalCamByCam.TryGetValue(currentCamera, out portalCamera);
            if (!portalCamera) {
                GameObject go = new GameObject("~" + currentCamera.name + "->" + m_Portal.name, typeof(Camera));
                //go.hideFlags = HideFlags.HideAndDontSave;
                go.hideFlags = HideFlags.DontSave;

                Camera camera = go.GetComponent<Camera>();
                camera.enabled = false;
                camera.transform.position = transform.position;
                camera.transform.rotation = transform.rotation;
                camera.gameObject.AddComponent<FlareLayer>();

                // TODO: Awake doesn't get called when using ExecuteInEditMode
                portalCamera = go.AddComponent<PortalCamera>();
                portalCamera.Awake();
                portalCamera.enterScene = this.gameObject.scene;
                portalCamera.exitScene = m_Portal.ExitPortal ? m_Portal.ExitPortal.gameObject.scene : this.gameObject.scene;
                portalCamera.parent = currentCamera;
                portalCamera.portal = m_Portal;

                if (m_Portal.ExitPortal && this.gameObject.scene != m_Portal.ExitPortal.gameObject.scene) {
                    ////PortalCameraRenderSettings thing = go.AddComponent<PortalCameraRenderSettings>();
                    ////thing.scene = exitPortal.gameObject.scene;
                }

                m_PortalCamByCam[currentCamera] = portalCamera;
            }
            return portalCamera;
        }

        private static Mesh MakePortalMesh() {
            // Front:
            //  1  2
            //  0  3

            // Back
            //  5  6
            //  4  7
            Vector3[] vertices = new Vector3[] {
                // Front
                new Vector3(-0.5f, -0.5f, 0),
                new Vector3(-0.5f,  0.5f, 0),
                new Vector3(0.5f,  0.5f, 0),
                new Vector3(0.5f, -0.5f, 0),
                
                // Back
                new Vector3(-0.5f, -0.5f, 1.0f),
                new Vector3(-0.5f,  0.5f, 1.0f),
                new Vector3(0.5f,  0.5f, 1.0f),
                new Vector3(0.5f, -0.5f, 1.0f),
            };

            Vector2[] uvs = new Vector2[] {
                // Front
                new Vector2(0, 0),
                new Vector2(0, 1),
                new Vector2(1, 1),
                new Vector2(1, 0),

                // Back
                new Vector2(0, 0),
                new Vector2(0, 1),
                new Vector2(1, 1),
                new Vector2(1, 0),
            };

            int[] frontFaceTriangles = new int[] {
                // Front
                0, 1, 2,
                2, 3, 0
            };

            int[] backFaceTriangles = new int[] {
                // Left
                0, 1, 5,
                5, 4, 0,

                // Back
                4, 5, 6,
                6, 7, 4,

                // Right
                7, 6, 2,
                2, 3, 7,

                // Top
                6, 5, 1,
                1, 2, 6,

                // Bottom
                0, 4, 7,
                7, 3, 0
            };

            Mesh mesh = new Mesh();
            mesh.name = "Portal Mesh";
            mesh.subMeshCount = 2;
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.SetTriangles(frontFaceTriangles, 0);
            mesh.SetTriangles(backFaceTriangles, 1);

            return mesh;
        }
        #endregion
    }
}
