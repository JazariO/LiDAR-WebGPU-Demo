using System;
using Random = UnityEngine.Random;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.VFX;
using TMPro;
using System.Runtime.InteropServices;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class LiDARController : MonoBehaviour
{
    [StructLayout(LayoutKind.Sequential)]
    [Serializable] struct LidarPoint
    {
        public Vector3 position;
    }

    [Serializable] class LidarPointCloud
    {
        public int current_index;
        public GraphicsBuffer gpu_buffer;
        public GameObject lidar_vfx_graph_prefab_instance;
    }
    
    // References
    [SerializeField] InputActionReference input_action_reference_lidar_move;
    [SerializeField] InputActionReference input_action_reference_lidar_look;
    [SerializeField] InputActionReference input_action_reference_lidar_scan_radius;
    [SerializeField] InputActionReference input_action_reference_lidar_scan_fire;
    [SerializeField] InputActionReference input_action_reference_lidar_scan_reset;
    [SerializeField] Camera lidar_camera;
    [SerializeField] GameObject lidar_vfx_graph_prefab_asset;
    [SerializeField] Transform scan_origin_transform;
    [SerializeField] Transform scan_target_transform;
    [SerializeField] LineRenderer scan_line_renderer;
    [SerializeField] TMP_Text particle_budget_TMP;
    [SerializeField] TMP_Text particles_cast_TMP;
    [SerializeField] TMP_Text scanner_radius_TMP;
    [SerializeField] LiDARUserSettingsDataSO lidar_user_settings_data_SO;

    // Properties
    [SerializeField] LayerMask lidar_layer_mask;
    [SerializeField] float scan_radius_change_speed = 0.01f;
    [SerializeField] float scan_distance_limit_min = 2f;
    [SerializeField] float scan_distance_limit_max = 80f;
    [SerializeField] Gradient scan_color_gradient;
    [SerializeField] int point_count_max; // points in each cloud

    // Internals
    private LidarPointCloud[] lidarPointCloudArray;
    const int point_cloud_count_max = 1; // total number of clouds
    private int point_cloud_index_current = 0;
    private VisualEffect current_visual_effect_graph;

    // Visualization
    private static readonly int lidar_vfx_reference_graphicsBuffer = Shader.PropertyToID("LiDAR_GraphicsBuffer");
    private static readonly int lidar_vfx_reference_camera_position = Shader.PropertyToID("LiDAR_Camera_Position");
    private static readonly int lidar_vfx_reference_particle_count = Shader.PropertyToID("LiDAR_Particle_Count");
    private static readonly int lidar_vfx_reference_distance_limit_min = Shader.PropertyToID("Distance_Limit_Min");
    private static readonly int lidar_vfx_reference_distance_limit_max = Shader.PropertyToID("Distance_Limit_Max");

    private float scan_radius = 0.2f;
    private LidarPoint[] frame_batch;
    private int scan_amount = 60;

    private void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        scan_line_renderer.enabled = false;

        // Pre-allocate single block for all point cloud graphics buffer initializations
        const int chunkSize = 8192;
        var block = new LidarPoint[chunkSize];
        for(int j = 0; j < chunkSize; j++)
            block[j] = new LidarPoint { position = Vector3.one * -100000 };

        lidarPointCloudArray = new LidarPointCloud[point_cloud_count_max];
        LidarPointCloud cloud;
        for(int i = 0; i < point_cloud_count_max; i++)
        {
            cloud = new LidarPointCloud
            {
                current_index = 0,
                gpu_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, point_count_max, sizeof(float) * 3), // Vector3(3 floats) = 12bytes
                lidar_vfx_graph_prefab_instance = null
            };
            lidarPointCloudArray[i] = cloud;

            // Prefill graphics buffer with distant position for all cloud points
            {
                GraphicsBuffer buffer = cloud.gpu_buffer;

                // Copy block into buffer repeatedly
                for(int offset = 0; offset < point_count_max; offset += chunkSize)
                {
                    int count = Mathf.Min(chunkSize, point_count_max - offset);
                    buffer.SetData(block, 0, offset, count);
                }
            }
        }

        // Initialize first point cloud
        cloud = lidarPointCloudArray[point_cloud_index_current];
        cloud.lidar_vfx_graph_prefab_instance = Instantiate(lidar_vfx_graph_prefab_asset, Vector3.zero, Quaternion.identity);
        current_visual_effect_graph = cloud.lidar_vfx_graph_prefab_instance.GetComponent<VisualEffect>();

        current_visual_effect_graph.SetGraphicsBuffer(lidar_vfx_reference_graphicsBuffer, cloud.gpu_buffer);
        current_visual_effect_graph.SetVector3(lidar_vfx_reference_camera_position, lidar_camera.transform.position);
        current_visual_effect_graph.SetInt(lidar_vfx_reference_particle_count, point_count_max);
        current_visual_effect_graph.SetFloat(lidar_vfx_reference_distance_limit_min, scan_distance_limit_min);
        current_visual_effect_graph.SetFloat(lidar_vfx_reference_distance_limit_max, scan_distance_limit_max);
        current_visual_effect_graph.Play();

        //Initialize Frame batch
        frame_batch = new LidarPoint[scan_amount];
    }

    private void OnDestroy()
    {
        // Release all cloud GPU buffers
        if(lidarPointCloudArray != null)
        {
            for(int i = 0; i < lidarPointCloudArray.Length; i++)
            {
                var cloud = lidarPointCloudArray[i];
                if(cloud != null && cloud.gpu_buffer != null)
                {
                    cloud.gpu_buffer.Release();
                    cloud.gpu_buffer = null;
                    if(cloud.lidar_vfx_graph_prefab_instance != null)
                    {
                        Destroy(cloud.lidar_vfx_graph_prefab_instance);
                        cloud.lidar_vfx_graph_prefab_instance = null;
                    }
                }
            }
        }
    }

    private void Update()
    {
        float particle_budget_percentage = (1 - (float)lidarPointCloudArray[point_cloud_index_current].current_index / point_count_max) * 100;
        particle_budget_TMP.text = $"Particle Budget Remaining: {particle_budget_percentage:F2}%";
        particles_cast_TMP.text = $"Particles Cast: {lidarPointCloudArray[point_cloud_index_current].current_index:N0}/{point_count_max:N0}";

        // move lidar
        Vector2 raw_move_input = input_action_reference_lidar_move.action.ReadValue<Vector2>();
        Vector3 move_input = new Vector3(raw_move_input.x, 0, raw_move_input.y);

        if(move_input.sqrMagnitude > 0f)
        {
            Vector3 move_direction = transform.TransformDirection(move_input.normalized);
            transform.position += 5f * Time.deltaTime * move_direction;
        }


        // look input
        Vector2 raw = input_action_reference_lidar_look.action.ReadValue<Vector2>();
        if(raw.sqrMagnitude > 0f)
        {
            // yaw
            transform.Rotate(Vector3.up, raw.x * lidar_user_settings_data_SO.look_sensitivity, Space.World);

            // pitch with clamp
            float pitch = lidar_camera.transform.localEulerAngles.x;
            if(pitch > 180f) pitch -= 360f;
            pitch = Mathf.Clamp(pitch - raw.y * lidar_user_settings_data_SO.look_sensitivity, -88f, 88f);

            lidar_camera.transform.localEulerAngles = new Vector3(pitch, 0f, 0f);
        }

        // change scanner radius
        scan_radius += Time.deltaTime * scan_radius_change_speed * input_action_reference_lidar_scan_radius.action.ReadValue<float>();
        scan_radius = Mathf.Clamp(scan_radius, 0f, 1f);
        scanner_radius_TMP.text = $"Scanner Radius: {scan_radius:F2}m";

        // update vfx graph camera position
        current_visual_effect_graph.SetVector3(lidar_vfx_reference_camera_position, lidar_camera.transform.position);

        // handle input, fire raycast, and update gpu buffer for point cloud
        if(input_action_reference_lidar_scan_fire.action.IsPressed())
        {
            int hits = 0;
            for(int i = 0; i < scan_amount; i++)
            {
                Vector3 random_pos_target = Random.insideUnitSphere * scan_radius;
                Vector3 random_scanline = random_pos_target + scan_target_transform.position;
                Ray scan_ray = new Ray(scan_origin_transform.position, random_scanline - scan_origin_transform.position);

                if(Physics.Raycast(scan_ray, out RaycastHit hit_info, 1000f, lidar_layer_mask))
                {
                    frame_batch[hits++].position = hit_info.point;
                    scan_line_renderer.enabled = true;
                    scan_line_renderer.SetPosition(0, scan_origin_transform.localPosition);
                    scan_line_renderer.SetPosition(1, scan_origin_transform.InverseTransformPoint(hit_info.point));

                    float remapped_distance = Mathf.InverseLerp(scan_distance_limit_min, scan_distance_limit_max, hit_info.distance);
                    Color scan_color = scan_color_gradient.Evaluate(remapped_distance);
                    scan_line_renderer.startColor = scan_color;
                    scan_line_renderer.endColor = scan_color;
                } else
                {
                    // Missed raycast
                    scan_line_renderer.enabled = false;
                }
            }

            if(hits > 0)
            { 
                LidarPointCloud cloud = lidarPointCloudArray[point_cloud_index_current];
                int current_index = cloud.current_index;

                // Upload the modified element to the gpu by aligning the structured gpu buffer with the cpu array
                int firstCount = Mathf.Min(hits, point_count_max - current_index);
                cloud.gpu_buffer.SetData(frame_batch, 0, current_index, firstCount);

                int wrapCount = hits - firstCount;
                if(wrapCount > 0)
                {
                    cloud.gpu_buffer.SetData(frame_batch, firstCount, 0, wrapCount);
                }

                // Advance ring buffer index
                cloud.current_index = (current_index + hits) % point_count_max;
            }
        }
        else
        {
            //not pressed
            scan_line_renderer.enabled = false;
        }


        // check for reset lidar input
        if(input_action_reference_lidar_scan_reset.action.WasPressedThisFrame()) 
        {
            // reset current index
            point_cloud_index_current = 0;

            // Pre-allocate single block for current point cloud graphics buffer
            const int chunkSize = 8192;
            var block = new LidarPoint[chunkSize];
            for(int j = 0; j < chunkSize; j++)
                block[j] = new LidarPoint { position = Vector3.one * -100000 };

            // reset graphics buffer with distant position for all cloud points
            {
                GraphicsBuffer buffer = lidarPointCloudArray[point_cloud_index_current].gpu_buffer;

                // Copy block into buffer repeatedly
                for(int offset = 0; offset < point_count_max; offset += chunkSize)
                {
                    int count = Mathf.Min(chunkSize, point_count_max - offset);
                    buffer.SetData(block, 0, offset, count);
                }
            }
        }
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = Color.darkCyan;
#if UNITY_EDITOR
        Handles.DrawWireDisc(scan_target_transform.position, lidar_camera.transform.forward, scan_radius);
#endif
    }
}
