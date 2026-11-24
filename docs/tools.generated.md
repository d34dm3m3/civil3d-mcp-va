# Generated Civil 3D tool reference

Generated from the runtime manifest for civil3d-mcp 1.2.1. Do not edit by hand.

- Catalog entries: 207
- Domains: 30

| Tool | Domain | Operations | Plugin methods | Safe retry |
|---|---|---|---|---|
| `civil3d_alignment` | alignment | list, get, station_to_point, point_to_station, create, delete, report, add_tangent, add_spiral, delete_entity, set_station_equation, get_station_offset, offset_create, widen_transition, entities_get, replace_curve_with_lines, raw | listAlignments, getAlignment, alignmentStationToPoint, alignmentPointToStation, createAlignment, deleteAlignment, alignmentSampleStations, alignmentAddTangent, alignmentAddSpiral, alignmentDeleteEntity, alignmentSetStationEquation, alignmentGetStationOffset, alignmentOffsetCreate, alignmentWidenTransition, alignmentEntitiesGet, alignmentReplaceCurveWithLines | no |
| `civil3d_alignment_add_spiral` | alignment | — | alignmentAddSpiral | no |
| `civil3d_alignment_add_tangent` | alignment | — | alignmentAddTangent | no |
| `civil3d_alignment_delete_entity` | alignment | — | alignmentDeleteEntity | no |
| `civil3d_alignment_get_station_offset` | alignment | — | alignmentGetStationOffset | yes |
| `civil3d_alignment_offset_create` | alignment | — | alignmentOffsetCreate | no |
| `civil3d_alignment_report` | alignment | — | getAlignment, alignmentSampleStations | yes |
| `civil3d_alignment_set_station_equation` | alignment | — | alignmentSetStationEquation | no |
| `civil3d_alignment_widen_transition` | alignment | — | alignmentWidenTransition | no |
| `civil3d_intersection` | alignment | list, get | listIntersections, getIntersection | yes |
| `civil3d_intersection_get` | alignment | — | getIntersection | yes |
| `civil3d_intersection_list` | alignment | — | listIntersections | yes |
| `civil3d_superelevation` | alignment | get, set, report, raw | getSuperelevation, setSuperelevation, generateSuperelevationReport | no |
| `civil3d_superelevation_design_check` | alignment | — | checkSuperelevationDesign | yes |
| `civil3d_superelevation_get` | alignment | — | getSuperelevation | yes |
| `civil3d_superelevation_report` | alignment | — | generateSuperelevationReport | no |
| `civil3d_superelevation_set` | alignment | — | setSuperelevation | no |
| `civil3d_assembly` | assembly | list, get, create, create_subassembly, edit, raw | listAssemblies, getAssembly, createAssembly, createSubassembly, editAssembly | no |
| `civil3d_assembly_create` | assembly | — | createAssembly | no |
| `civil3d_assembly_edit` | assembly | — | editAssembly | no |
| `civil3d_subassembly_create` | assembly | — | createSubassembly | no |
| `civil3d_coordinate_system` | coordinate_system | info, transform | getCoordinateSystemInfo, transformCoordinates | yes |
| `civil3d_corridor` | corridor | list, get, rebuild, get_surfaces, get_feature_lines, compute_volumes, summary, target_mapping_get, target_mapping_set, region_add, region_edit, region_split, region_delete, create, delete, surface_create, surface_boundary_add, quantities, raw | listCorridors, getCorridor, rebuildCorridor, getCorridorSurfaces, getCorridorFeatureLines, computeCorridorVolumes, getCorridorTargetMappings, setCorridorTargetMappings, addCorridorRegion, editCorridorRegion, splitCorridorRegion, deleteCorridorRegion, createCorridor, deleteCorridor, createCorridorSurface, addCorridorSurfaceBoundary, corridorQuantities | no |
| `civil3d_corridor_region_add` | corridor | — | addCorridorRegion | no |
| `civil3d_corridor_region_delete` | corridor | — | deleteCorridorRegion | no |
| `civil3d_corridor_summary` | corridor | — | getCorridor, getCorridorSurfaces, computeCorridorVolumes | yes |
| `civil3d_corridor_target_mapping_get` | corridor | — | getCorridorTargetMappings | yes |
| `civil3d_corridor_target_mapping_set` | corridor | — | setCorridorTargetMappings | no |
| `civil3d_cost_estimation` | cost_estimation | pay_items_export, material_cost_estimate, raw | exportPayItems, calculateMaterialCostEstimate | no |
| `civil3d_material_cost_estimate` | cost_estimation | — | calculateMaterialCostEstimate | no |
| `civil3d_pay_items_export` | cost_estimation | — | exportPayItems | no |
| `civil3d_detention` | detention | basin_size_calculate, stage_storage, raw | calculateDetentionBasinSize, calculateDetentionStageStorage | no |
| `civil3d_detention_basin_size_calculate` | detention | — | calculateDetentionBasinSize | yes |
| `civil3d_detention_stage_storage` | detention | — | calculateDetentionStageStorage | yes |
| `civil3d_docs` | docs | list_tool_capabilities, orchestrate, raw | — | no |
| `civil3d_orchestrate` | docs | — | — | yes |
| `list_tool_capabilities` | docs | — | — | yes |
| `civil3d_drawing` | drawing | info, new, save, undo, redo, settings, selected_objects_info, list_object_types, layer_stats, style_set, layer_list, layer_set, entities_list, entities_layer_set, layout_list, block_attributes_get, block_attributes_set, layout_text_add, corridor_feature_lines_set, scale_get, scale_set, view_png, mleader_add, entities_erase, block_list_external, block_import, block_insert, layout_create, layout_delete, viewport_create, viewport_set, viewport_erase, sysvar_get, sysvar_set, layout_plot, entities_style_set, linetypes_list, textstyles_list, textstyle_set, block_definition_get, block_definition_set, plot_media_list, page_setups_list, page_setup_set, page_setup_apply, plugin_reload, object_classes_list, modules_list, save_test, solids_history_remove, block_reference_get, block_reference_set, styles_list_external, styles_import, settings_ambient_get, settings_ambient_set, blocks_purge, mleader_style_set, block_entities_color_set, band_sets_list, band_set_set, block_entities_edit, block_references_replace, layers_import, linetypes_load, object_layers_get, object_layers_set, layer_states_list, layer_state_save, layer_state_restore, layer_state_delete, grid_crosses_add, north_arrow_align, layers_purge, label_styles_list, label_style_text_get, label_style_text_set, label_style_copy, profile_view_axis_get, profile_view_axis_set, band_style_text_set, civil_settings_list, civil_settings_get, civil_settings_set, structure_bend_angles, documents_list, document_open, document_activate, document_close, drawing_transform, xrefs_list, xref_attach, xref_detach, xref_unload, xref_reload, label_sets_list, label_style_properties_get, label_style_properties_set, label_expressions_list, label_expression_set, label_expression_delete, style_display_get, style_display_set, entities_id_set, entities_id_clear, entities_move, mleader_mask_set, raster_attach, raster_detach, raw | getDrawingInfo, newDrawing, saveDrawing, undoDrawing, redoDrawing, getDrawingSettings, getSelectedCivilObjectsInfo, listCivilObjectTypes, getDrawingLayerStats, setObjectStyle, listLayers, setLayers, listEntities, setEntitiesLayer, listLayouts, getBlockAttributes, setBlockAttributes, addLayoutText, setCorridorFeatureLines, getScale, setScale, plotViewPng, addMLeader, eraseEntities, listExternalBlocks, importBlocks, insertBlock, createLayout, deleteLayout, createViewport, setViewport, eraseViewport, getSystemVariable, setSystemVariable, plotLayout, setEntitiesStyle, listLinetypes, listTextStyles, setTextStyle, getBlockDefinition, setBlockDefinition, listPlotMedia, listPageSetups, setPageSetup, applyPageSetup, reloadPlugin, listObjectClasses, listLoadedModules, saveTest, replaceSolidsWithoutHistory, getBlockReference, setBlockReference, listExternalStyles, importStyles, getAmbientSettings, setAmbientSettings, purgeBlocks, setMLeaderStyle, setBlockEntitiesColor, listBandSets, setBandSet, editBlockEntities, replaceBlockReferences, layersImport, linetypesLoad, objectLayersGet, objectLayersSet, layerStatesList, layerStateSave, layerStateRestore, layerStateDelete, gridCrossesAdd, northArrowAlign, layersPurge, labelStylesList, labelStyleTextGet, labelStyleTextSet, labelStyleCopy, profileViewAxisGet, profileViewAxisSet, bandStyleTextSet, civilSettingsList, civilSettingsGet, civilSettingsSet, structureBendAngles, documentsList, documentOpen, documentActivate, documentClose, drawingTransform, xrefsList, xrefAttach, xrefDetach, xrefUnload, xrefReload, labelSetsList, labelStylePropertiesGet, labelStylePropertiesSet, labelExpressionsList, labelExpressionSet, labelExpressionDelete, styleDisplayGet, styleDisplaySet, entityIdsSet, entityIdsClear, moveEntities, setMLeaderMask, rasterAttach, rasterDetach | no |
| `get_drawing_info` | drawing | — | getDrawingInfo | yes |
| `get_selected_civil_objects_info` | drawing | — | getSelectedCivilObjectsInfo | yes |
| `list_civil_object_types` | drawing | — | listCivilObjectTypes | yes |
| `acad_create_3dpolyline` | geometry | — | create3dPolyline | no |
| `acad_create_mtext` | geometry | — | createMText | no |
| `acad_create_polyline` | geometry | — | createPolyline | no |
| `acad_create_text` | geometry | — | createText | no |
| `civil3d_cogo_curve_solve` | geometry | — | cogoCurveSolve | yes |
| `civil3d_cogo_direction_distance` | geometry | — | cogoDirectionDistance | yes |
| `civil3d_cogo_inverse` | geometry | — | cogoInverse | yes |
| `civil3d_cogo_traverse` | geometry | — | cogoTraverse | yes |
| `civil3d_geometry` | geometry | cogo_inverse, cogo_direction_distance, cogo_traverse, cogo_curve_solve, create_line_segment, create_polyline, create_3dpolyline, create_text, create_mtext, raw | cogoInverse, cogoDirectionDistance, cogoTraverse, cogoCurveSolve, createLineSegment, createPolyline, create3dPolyline, createText, createMText | no |
| `create_line_segment` | geometry | — | createLineSegment | no |
| `civil3d_feature_line` | grading | list, get, export_as_polyline | listFeatureLines, getFeatureLine, exportFeatureLineAsPolyline | no |
| `civil3d_feature_line_create` | grading | — | createFeatureLine | no |
| `civil3d_grading` | grading | group_list, group_get, group_create, group_delete, group_volume, group_surface_create, list, get, create, delete, criteria_list, feature_line_list, feature_line_get, feature_line_export_as_polyline, feature_line_create, raw | listGradingGroups, getGradingGroup, createGradingGroup, deleteGradingGroup, getGradingGroupVolume, createSurfaceFromGradingGroup, listGradings, getGrading, createGrading, deleteGrading, listGradingCriteria, listFeatureLines, getFeatureLine, exportFeatureLineAsPolyline, createFeatureLine | no |
| `civil3d_grading_create` | grading | — | createGrading | no |
| `civil3d_grading_criteria_list` | grading | — | listGradingCriteria | yes |
| `civil3d_grading_delete` | grading | — | deleteGrading | no |
| `civil3d_grading_get` | grading | — | getGrading | yes |
| `civil3d_grading_group_create` | grading | — | createGradingGroup | no |
| `civil3d_grading_group_delete` | grading | — | deleteGradingGroup | no |
| `civil3d_grading_group_get` | grading | — | getGradingGroup | yes |
| `civil3d_grading_group_list` | grading | — | listGradingGroups | yes |
| `civil3d_grading_group_surface_create` | grading | — | createSurfaceFromGradingGroup | no |
| `civil3d_grading_group_volume` | grading | — | getGradingGroupVolume | yes |
| `civil3d_grading_list` | grading | — | listGradings | yes |
| `civil3d_help` | help | search, search_videos, get_topic, status, reindex | — | yes |
| `civil3d_catchment` | hydrology | list_catchment_groups, get_catchment_group, list_catchments, get_catchment_properties, set_catchment_properties, copy_catchment_to_group, get_catchment_flow_path, get_catchment_boundary, raw | listCatchmentGroups, getCatchmentGroup, listCatchments, getCatchmentProperties, setCatchmentProperties, copyCatchmentToGroup, getCatchmentFlowPath, getCatchmentBoundary | no |
| `civil3d_hydrology` | hydrology | list_capabilities, trace_flow_path, find_low_point, estimate_runoff, delineate_watershed, calculate_catchment_area, list_catchment_groups, get_catchment_group, list_catchments, get_catchment_properties, set_catchment_properties, copy_catchment_to_group, get_catchment_flow_path, get_catchment_boundary, list_tc_methods, calculate_tc, generate_hydrograph, list_ssa_capabilities, export_stm, import_stm, open_storm_sanitary_analysis, watershed_runoff_workflow, runoff_detention_workflow, runoff_pipe_workflow, raw | listHydrologyCapabilities, traceHydrologyFlowPath, findHydrologyLowPoint, estimateHydrologyRunoff, delineateWatershed, calculateCatchmentArea, listCatchmentGroups, getCatchmentGroup, listCatchments, getCatchmentProperties, setCatchmentProperties, copyCatchmentToGroup, getCatchmentFlowPath, getCatchmentBoundary, listTcMethods, calculateTimeOfConcentration, generateHydrograph, listSsaCapabilities, exportStm, importStm, openStormSanitaryAnalysis, watershedRunoffWorkflow, runoffDetentionWorkflow, runoffPipeWorkflow | no |
| `civil3d_hydrology_runoff_detention_workflow` | hydrology | — | runoffDetentionWorkflow | yes |
| `civil3d_hydrology_runoff_pipe_workflow` | hydrology | — | runoffPipeWorkflow | yes |
| `civil3d_hydrology_watershed_runoff_workflow` | hydrology | — | watershedRunoffWorkflow | yes |
| `civil3d_stm` | hydrology | list_ssa_capabilities, export_stm, import_stm, open_storm_sanitary_analysis, raw | listSsaCapabilities, exportStm, importStm, openStormSanitaryAnalysis | no |
| `civil3d_time_of_concentration` | hydrology | list_tc_methods, calculate_tc, generate_hydrograph, raw | listTcMethods, calculateTimeOfConcentration, generateHydrograph | no |
| `civil3d_invoke` | invoke | list, read, write, raw | listCommands | no |
| `civil3d_job` | job | start, status, cancel, raw | startJob, getJobStatus, cancelJob | no |
| `civil3d_parcel` | parcel | list_sites, list, get, create, edit, lot_line_adjust, report, raw | listParcelSites, listParcels, getParcel, createParcel, editParcel, adjustParcelLotLine, reportParcels | no |
| `civil3d_parcel_create` | parcel | — | createParcel | no |
| `civil3d_parcel_edit` | parcel | — | editParcel | no |
| `civil3d_parcel_lot_line_adjust` | parcel | — | adjustParcelLotLine | no |
| `civil3d_parcel_report` | parcel | — | reportParcels | yes |
| `civil3d_pipe` | pipe | list, get, get_pipe, get_structure, check_interference, create, add_pipe, add_structure, catalog_list, calculate_hgl, hydraulic_analysis, get_structure_properties, size_network, automate_profile_view, network_profile_view_add, network_build, structure_rotation_set, network_delete, catalog_families, parts_list_family_add, parts_list_style_set, parts_list_sizes_add, parts_list_family_remove, parts_list_size_data_get, parts_list_size_data_set, network_labels_add, network_labels_delete, network_clearance, network_quantities, part_style_get, part_style_set, part_style_copy, list_pressure_networks, get_pressure_network, create_pressure_network, delete_pressure_network, assign_pressure_parts_list, set_pressure_cover, validate_pressure_network, export_pressure_network, connect_pressure_networks, add_pressure_pipe, get_pressure_pipe_properties, resize_pressure_pipe, add_pressure_fitting, get_pressure_fitting_properties, add_pressure_appurtenance, raw | listPipeNetworks, getPipeNetwork, getPipe, getStructure, checkPipeNetworkInterference, createPipeNetwork, addPipeToNetwork, addStructureToNetwork, listPipePartsCatalog, calculatePipeNetworkHgl, analyzePipeNetworkHydraulics, getPipeStructureProperties, resizePipeInNetwork, listProfiles, createProfileFromSurface, profileViewCreate, addNetworkToProfileView, buildPipeNetworkRun, setStructureRotation, deletePipeNetwork, listCatalogPartFamilies, addPartsListFamily, setPartsListFamilyStyle, addPartsListSizes, removePartsListFamily, partsListSizeDataGet, partsListSizeDataSet, addNetworkLabels, deleteNetworkLabels, networkClearance, networkQuantities, getPartStyle, setPartStyle, copyPartStyle, listPressureNetworks, getPressureNetworkInfo, createPressureNetwork, deletePressureNetwork, assignPressurePartsList, setPressureNetworkCover, validatePressureNetwork, exportPressureNetwork, connectPressureNetworks, addPressurePipe, getPressurePipeProperties, resizePressurePipe, addPressureFitting, getPressureFittingProperties, addPressureAppurtenance | no |
| `civil3d_pipe_catalog` | pipe | — | listPipePartsCatalog | yes |
| `civil3d_pipe_hydraulic_analysis` | pipe | — | analyzePipeNetworkHydraulics | yes |
| `civil3d_pipe_network` | pipe | list, get, get_pipe, get_structure, check_interference | listPipeNetworks, getPipeNetwork, getPipe, getStructure, checkPipeNetworkInterference | yes |
| `civil3d_pipe_network_edit` | pipe | create, add_pipe, add_structure, raw | createPipeNetwork, addPipeToNetwork, addStructureToNetwork | no |
| `civil3d_pipe_network_hgl_calculate` | pipe | — | calculatePipeNetworkHgl | yes |
| `civil3d_pipe_network_size` | pipe | — | getPipeNetwork, listPipePartsCatalog, resizePipeInNetwork | no |
| `civil3d_pipe_profile_view_automation` | pipe | — | getPipeNetwork, listProfiles, createProfileFromSurface, profileViewCreate | no |
| `civil3d_pipe_structure_properties` | pipe | — | getPipeStructureProperties | yes |
| `civil3d_pressure_appurtenance_add` | pipe | — | addPressureAppurtenance | no |
| `civil3d_pressure_fitting_add` | pipe | — | addPressureFitting | no |
| `civil3d_pressure_fitting_get_properties` | pipe | — | getPressureFittingProperties | yes |
| `civil3d_pressure_network_assign_parts_list` | pipe | — | assignPressurePartsList | no |
| `civil3d_pressure_network_connect` | pipe | — | connectPressureNetworks | no |
| `civil3d_pressure_network_create` | pipe | — | createPressureNetwork | no |
| `civil3d_pressure_network_delete` | pipe | — | deletePressureNetwork | no |
| `civil3d_pressure_network_export` | pipe | — | exportPressureNetwork | yes |
| `civil3d_pressure_network_get_info` | pipe | — | getPressureNetworkInfo | yes |
| `civil3d_pressure_network_list` | pipe | — | listPressureNetworks | yes |
| `civil3d_pressure_network_set_cover` | pipe | — | setPressureNetworkCover | no |
| `civil3d_pressure_network_validate` | pipe | — | validatePressureNetwork | yes |
| `civil3d_pressure_pipe_add` | pipe | — | addPressurePipe | no |
| `civil3d_pressure_pipe_get_properties` | pipe | — | getPressurePipeProperties | yes |
| `civil3d_pressure_pipe_resize` | pipe | — | resizePressurePipe | no |
| `civil3d_plan_production` | plan_production | sheet_set_list, sheet_set_get_info, sheet_set_create, sheet_add, sheet_get_properties, sheet_set_title_block, plan_profile_sheet_update_alignment, sheet_view_create, sheet_view_set_scale, sheet_publish_pdf, sheet_set_export, raw | listSheetSets, getSheetSetInfo, createSheetSet, addSheet, getSheetProperties, setSheetTitleBlock, updatePlanProfileSheetAlignment, createSheetView, setSheetViewScale, publishSheetPdf, exportSheetSet | no |
| `civil3d_plan_profile_sheet_update_alignment` | plan_production | — | updatePlanProfileSheetAlignment | no |
| `civil3d_sheet_add` | plan_production | — | addSheet | no |
| `civil3d_sheet_get_properties` | plan_production | — | getSheetProperties | yes |
| `civil3d_sheet_publish_pdf` | plan_production | — | publishSheetPdf | no |
| `civil3d_sheet_set_create` | plan_production | — | createSheetSet | no |
| `civil3d_sheet_set_export` | plan_production | — | exportSheetSet | no |
| `civil3d_sheet_set_get_info` | plan_production | — | getSheetSetInfo | yes |
| `civil3d_sheet_set_list` | plan_production | — | listSheetSets | yes |
| `civil3d_sheet_set_title_block` | plan_production | — | setSheetTitleBlock | no |
| `civil3d_sheet_view_create` | plan_production | — | createSheetView | no |
| `civil3d_sheet_view_set_scale` | plan_production | — | setSheetViewScale | no |
| `civil3d_health` | plugin | — | getCivil3DHealth | yes |
| `civil3d_point` | point | list, get, create, list_groups, import, delete, group_create, group_update, group_delete, export, transform, raw | listCogoPoints, getCogoPoint, createCogoPoints, listPointGroups, importCogoPoints, deleteCogoPoints, createPointGroup, updatePointGroup, deletePointGroup, exportCogoPoints, transformCogoPoints | no |
| `civil3d_point_export` | point | — | exportCogoPoints | yes |
| `civil3d_point_group_create` | point | — | createPointGroup | no |
| `civil3d_point_group_delete` | point | — | deletePointGroup | no |
| `civil3d_point_group_update` | point | — | updatePointGroup | no |
| `civil3d_point_transform` | point | — | transformCogoPoints | no |
| `create_cogo_point` | point | — | createCogoPoints | no |
| `civil3d_profile` | profile | list, get, get_elevation, sample_elevations, create_from_surface, create_layout, create_from_network, delete, report, add_pvi, delete_pvi, add_curve, set_grade, check_k_values, view_create, view_delete, view_band_set, view_band_set_apply, view_profiles_get, view_profiles_set, view_parts_get, view_parts_set, view_styles_list, view_style_set, view_range_set, band_styles_list, band_style_get, band_style_copy, band_style_set, view_bands_get, view_bands_set, view_band_edit, view_profile_labels_clear, label_styles_list, line_labels_add, label_style_get, label_style_copy, label_style_text_set, raw | listProfiles, getProfile, getProfileElevation, sampleProfileElevations, createProfileFromSurface, createLayoutProfile, profileFromNetwork, deleteProfile, profileAddPvi, profileDeletePvi, profileAddCurve, profileSetGrade, profileCheckKValues, profileViewCreate, profileViewDelete, profileViewBandSet, profileViewApplyBandSet, getProfileViewProfiles, setProfileViewProfiles, getProfileViewParts, setProfileViewParts, listProfileViewStyles, setProfileViewStyle, setProfileViewRange, listBandStyles, getBandStyle, copyBandStyle, setBandStyle, getProfileViewBands, setProfileViewBands, editProfileViewBands, clearProfileViewLabels, listProfileLabelStyles, addProfileLineLabels, getProfileLabelStyle, copyProfileLabelStyle, setProfileLabelStyleText | no |
| `civil3d_profile_add_curve` | profile | — | profileAddCurve | no |
| `civil3d_profile_add_pvi` | profile | — | profileAddPvi | no |
| `civil3d_profile_check_k_values` | profile | — | profileCheckKValues | yes |
| `civil3d_profile_delete_pvi` | profile | — | profileDeletePvi | no |
| `civil3d_profile_get_elevation` | profile | — | getProfileElevation | yes |
| `civil3d_profile_report` | profile | — | getProfile, sampleProfileElevations | yes |
| `civil3d_profile_set_grade` | profile | — | profileSetGrade | no |
| `civil3d_profile_view_band_set` | profile | — | profileViewBandSet | no |
| `civil3d_profile_view_create` | profile | — | profileViewCreate | no |
| `civil3d_data_shortcut` | project | list, sync, create_reference | listDataShortcuts, syncDataShortcuts, createDataShortcutReference | no |
| `civil3d_data_shortcut_create` | project | — | createDataShortcut | no |
| `civil3d_data_shortcut_promote` | project | — | promoteDataShortcut | no |
| `civil3d_data_shortcut_reference` | project | — | referenceDataShortcut | no |
| `civil3d_data_shortcut_sync` | project | — | syncDataShortcuts | no |
| `civil3d_project` | project | data_shortcut_list, data_shortcut_create, data_shortcut_promote, data_shortcut_reference, data_shortcut_sync, data_shortcut_create_reference, data_shortcut_project_get, data_shortcut_project_set, data_shortcut_publish, external_references_list, external_reference_set_path, raw | listDataShortcuts, createDataShortcut, promoteDataShortcut, referenceDataShortcut, syncDataShortcuts, createDataShortcutReference, getDataShortcutProject, setDataShortcutProject, publishDataShortcuts, listExternalReferences, setExternalReferencePath | no |
| `civil3d_qc` | qc | check_alignment, check_profile, check_corridor, check_pipe_network, check_surface, generate_report, raw | qcCheckAlignment, qcCheckProfile, qcCheckCorridor, qcCheckPipeNetwork, qcCheckSurface, qcReportGenerate | no |
| `civil3d_qc_check_alignment` | qc | — | qcCheckAlignment | yes |
| `civil3d_qc_check_corridor` | qc | — | qcCheckCorridor | yes |
| `civil3d_qc_check_pipe_network` | qc | — | qcCheckPipeNetwork | yes |
| `civil3d_qc_check_profile` | qc | — | qcCheckProfile | yes |
| `civil3d_qc_check_surface` | qc | — | qcCheckSurface | yes |
| `civil3d_qc_report_generate` | qc | — | qcReportGenerate | no |
| `civil3d_qty_alignment_lengths` | quantity_takeoff | — | qtyAlignmentLengths | yes |
| `civil3d_qty_earthwork_summary` | quantity_takeoff | — | qtyEarthworkSummary | yes |
| `civil3d_qty_export_to_csv` | quantity_takeoff | — | qtyExportToCsv | no |
| `civil3d_qty_parcel_areas` | quantity_takeoff | — | qtyParcelAreas | yes |
| `civil3d_qty_pipe_network_lengths` | quantity_takeoff | — | qtyPipeNetworkLengths | yes |
| `civil3d_qty_point_count_by_group` | quantity_takeoff | — | qtyPointCountByGroup | yes |
| `civil3d_qty_pressure_network_lengths` | quantity_takeoff | — | qtyPressureNetworkLengths | yes |
| `civil3d_qty_surface_volume` | quantity_takeoff | — | qtySurfaceVolume | yes |
| `civil3d_quantity_takeoff` | quantity_takeoff | surface_volume, pipe_network_lengths, pressure_network_lengths, parcel_areas, alignment_lengths, point_count_by_group, export_to_csv, earthwork_summary, raw | qtySurfaceVolume, qtyPipeNetworkLengths, qtyPressureNetworkLengths, qtyParcelAreas, qtyAlignmentLengths, qtyPointCountByGroup, qtyExportToCsv, qtyEarthworkSummary | no |
| `civil3d_section` | section | list_sample_lines, get_section_data, create_sample_lines, view_create, view_list, view_update_style, view_group_create, view_export, raw | listSampleLineGroups, getSectionData, createSampleLines, createSectionViews, listSectionViews, updateSectionViewStyles, createSectionViewGroup, exportSectionData | no |
| `civil3d_section_view_create` | section | — | createSectionViews | no |
| `civil3d_section_view_export` | section | — | exportSectionData | no |
| `civil3d_section_view_group_create` | section | — | createSectionViewGroup | no |
| `civil3d_section_view_list` | section | — | listSectionViews | yes |
| `civil3d_section_view_update_style` | section | — | updateSectionViewStyles | no |
| `civil3d_sight_distance` | sight_distance | calculate, stopping_distance_check | calculateSightDistance, checkStoppingDistance | yes |
| `civil3d_sight_distance_calculate` | sight_distance | — | calculateSightDistance | yes |
| `civil3d_stopping_distance_check` | sight_distance | — | checkStoppingDistance | yes |
| `civil3d_slope_analysis` | slope_analysis | — | calculateSlopeGeometry | yes |
| `civil3d_slope_geometry_calculate` | slope_analysis | — | calculateSlopeGeometry | yes |
| `civil3d_label` | standards | list, add, list_styles | listLabels, addLabel, listLabelStyles | no |
| `civil3d_qc_check_drawing_standards` | standards | — | qcCheckDrawingStandards | yes |
| `civil3d_qc_check_labels` | standards | — | qcCheckLabels | yes |
| `civil3d_qc_fix_drawing_standards` | standards | — | qcFixDrawingStandards | no |
| `civil3d_standards` | standards | label_list, label_add, label_list_styles, style_list, style_get, lookup, check_labels, check_drawing_standards, fix_drawing_standards, raw | listLabels, addLabel, listLabelStyles, listStyles, getStyle, qcCheckLabels, qcCheckDrawingStandards, qcFixDrawingStandards | no |
| `civil3d_standards_lookup` | standards | — | — | yes |
| `civil3d_style` | standards | list, get | listStyles, getStyle | yes |
| `civil3d_surface` | surface | list, get, get_elevation, get_elevation_along, get_statistics, create, delete, add_points, add_breakline, add_boundary, extract_contours, compute_volume, volume_calculate, volume_report, volume_by_region, analyze_slope, analyze_elevation, analyze_directions, watershed_add, contour_interval_set, statistics_get, sample_elevations, create_from_dem, comparison_workflow, drainage_workflow, paste, simplify, build_options_set, find_gaps, export_landxml, raw | listSurfaces, getSurface, getSurfaceElevation, getSurfaceElevationsAlong, getSurfaceStatistics, createSurface, deleteSurface, addSurfacePoints, addSurfaceBreakline, addSurfaceBoundary, extractSurfaceContours, computeSurfaceVolume, calculateSurfaceVolume, getSurfaceVolumeReport, calculateSurfaceVolumeByRegion, analyzeSurfaceSlope, analyzeSurfaceElevation, analyzeSurfaceDirections, addSurfaceWatershed, setSurfaceContourInterval, getSurfaceStatisticsDetailed, sampleSurfaceElevations, createSurfaceFromDem, traceHydrologyFlowPath, estimateHydrologyRunoff, pasteSurface, simplifySurface, setSurfaceBuildOptions, findSurfaceGaps, exportLandXml | no |
| `civil3d_surface_analyze_directions` | surface | — | analyzeSurfaceDirections | yes |
| `civil3d_surface_analyze_elevation` | surface | — | analyzeSurfaceElevation | yes |
| `civil3d_surface_analyze_slope` | surface | — | analyzeSurfaceSlope | yes |
| `civil3d_surface_comparison_workflow` | surface | — | getSurface, computeSurfaceVolume | yes |
| `civil3d_surface_contour_interval_set` | surface | — | setSurfaceContourInterval | no |
| `civil3d_surface_create_from_dem` | surface | — | createSurfaceFromDem | no |
| `civil3d_surface_drainage_workflow` | surface | — | getSurface, traceHydrologyFlowPath, getSurfaceElevationsAlong, estimateHydrologyRunoff | yes |
| `civil3d_surface_edit` | surface | add_points, add_breakline, add_boundary, extract_contours, compute_volume, raw | addSurfacePoints, addSurfaceBreakline, addSurfaceBoundary, extractSurfaceContours, computeSurfaceVolume | no |
| `civil3d_surface_sample_elevations` | surface | — | sampleSurfaceElevations | yes |
| `civil3d_surface_statistics_get` | surface | — | getSurfaceStatisticsDetailed | yes |
| `civil3d_surface_volume_by_region` | surface | — | calculateSurfaceVolumeByRegion | yes |
| `civil3d_surface_volume_calculate` | surface | — | calculateSurfaceVolume | yes |
| `civil3d_surface_volume_report` | surface | — | getSurfaceVolumeReport | yes |
| `civil3d_surface_watershed_add` | surface | — | addSurfaceWatershed | no |
| `civil3d_survey` | survey | database_list, figure_list, figure_get, observation_list | listSurveyDatabases, listSurveyFigures, getSurveyFigure, listSurveyObservations | yes |
| `civil3d_survey_database_list` | survey | — | listSurveyDatabases | yes |
| `civil3d_survey_figure_get` | survey | — | getSurveyFigure | yes |
| `civil3d_survey_figure_list` | survey | — | listSurveyFigures | yes |
| `civil3d_survey_observation_list` | survey | — | listSurveyObservations | yes |
| `civil3d_workflow` | workflow | corridor_qc_report, grading_surface_volume, surface_comparison_report, data_shortcut_publish_sync, data_shortcut_reference_sync, project_startup, project_reference_setup, drawing_readiness_audit, feature_line_to_grading, pipe_network_design, plan_production_publish, qc_fix_and_verify, raw | corridorQcReportWorkflow, calculateSurfaceVolume, surfaceComparisonReportWorkflow, dataShortcutPublishSyncWorkflow, dataShortcutReferenceSyncWorkflow, projectStartupWorkflow, projectReferenceSetupWorkflow, drawingReadinessAuditWorkflow, featureLineToGradingWorkflow, getPipeNetwork, listPipePartsCatalog, resizePipeInNetwork, analyzePipeNetworkHydraulics, planProductionPublishWorkflow, qcFixAndVerifyWorkflow | no |
| `civil3d_workflow_corridor_qc_report` | workflow | — | corridorQcReportWorkflow | no |
| `civil3d_workflow_data_shortcut_publish_sync` | workflow | — | dataShortcutPublishSyncWorkflow | no |
| `civil3d_workflow_data_shortcut_reference_sync` | workflow | — | dataShortcutReferenceSyncWorkflow | no |
| `civil3d_workflow_drawing_readiness_audit` | workflow | — | drawingReadinessAuditWorkflow | yes |
| `civil3d_workflow_feature_line_to_grading` | workflow | — | featureLineToGradingWorkflow | no |
| `civil3d_workflow_grading_surface_volume` | workflow | — | calculateSurfaceVolume | yes |
| `civil3d_workflow_pipe_network_design` | workflow | — | getPipeNetwork, listPipePartsCatalog, resizePipeInNetwork, analyzePipeNetworkHydraulics | no |
| `civil3d_workflow_plan_production_publish` | workflow | — | planProductionPublishWorkflow | no |
| `civil3d_workflow_project_reference_setup` | workflow | — | projectReferenceSetupWorkflow | no |
| `civil3d_workflow_project_startup` | workflow | — | projectStartupWorkflow | no |
| `civil3d_workflow_qc_fix_and_verify` | workflow | — | qcFixAndVerifyWorkflow | no |
| `civil3d_workflow_surface_comparison_report` | workflow | — | surfaceComparisonReportWorkflow | yes |
