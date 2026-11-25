using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

public enum CommandKind { Read, Write }

/// <summary>A plugin command: its name (the JSON-RPC method), domain (the class it lives in), whether it only reads the
/// drawing, and its handler.</summary>
public sealed record CommandInfo(string Name, string Domain, CommandKind Kind, Func<JsonObject?, CancellationToken, Task<object?>> Handler);

/// <summary>
/// C152 (2026-10-21): every plugin command in one table, replacing the switch in CommandDispatcher. Generated once from
/// the switch; maintained by hand since. Kind: Read only for commands that do not change the drawing or files (the Node
/// side lets them through without approval); when unsure, Write. listCommands (C153) returns this table.
/// </summary>
public static class CommandRegistry
{
  private const CommandKind R_ = CommandKind.Read, W_ = CommandKind.Write;

  public static readonly IReadOnlyDictionary<string, CommandInfo> All = Build();

  private static Dictionary<string, CommandInfo> Build()
  {
    var all = new Dictionary<string, CommandInfo>(StringComparer.Ordinal);
    void R(string name, string domain, CommandKind kind, Func<JsonObject?, CancellationToken, Task<object?>> handler) =>
      all.Add(name, new CommandInfo(name, domain, kind, handler));
    R("getCivil3DHealth", "Drawing", R_, (p, ct) => DrawingCommands.GetCivil3DHealthAsync());
    R("getDrawingInfo", "Drawing", R_, (p, ct) => DrawingCommands.GetDrawingInfoAsync());
    R("getProjectContext", "Drawing", R_, (p, ct) => DrawingCommands.GetProjectContextAsync(p));
    R("getDrawingSettings", "Drawing", W_, (p, ct) => DrawingCommands.GetDrawingSettingsAsync());
    R("getCoordinateSystemInfo", "CoordinateSystem", R_, (p, ct) => CoordinateSystemCommands.GetCoordinateSystemInfoAsync());
    R("transformCoordinates", "CoordinateSystem", R_, (p, ct) => CoordinateSystemCommands.TransformCoordinatesAsync(p));
    R("corridorQcReportWorkflow", "Workflow", W_, (p, ct) => WorkflowCommands.CorridorQcReportWorkflowAsync(p));
    R("surfaceComparisonReportWorkflow", "Workflow", W_, (p, ct) => WorkflowCommands.SurfaceComparisonReportWorkflowAsync(p));
    R("dataShortcutPublishSyncWorkflow", "Workflow", W_, (p, ct) => WorkflowCommands.DataShortcutPublishSyncWorkflowAsync(p));
    R("dataShortcutReferenceSyncWorkflow", "Workflow", W_, (p, ct) => WorkflowCommands.DataShortcutReferenceSyncWorkflowAsync(p));
    R("projectStartupWorkflow", "Workflow", W_, (p, ct) => WorkflowCommands.ProjectStartupWorkflowAsync(p));
    R("projectReferenceSetupWorkflow", "Workflow", W_, (p, ct) => WorkflowCommands.ProjectReferenceSetupWorkflowAsync(p));
    R("drawingReadinessAuditWorkflow", "Workflow", W_, (p, ct) => WorkflowCommands.DrawingReadinessAuditWorkflowAsync(p));
    R("featureLineToGradingWorkflow", "Workflow", W_, (p, ct) => WorkflowCommands.FeatureLineToGradingWorkflowAsync(p));
    R("planProductionPublishWorkflow", "Workflow", W_, (p, ct) => WorkflowCommands.PlanProductionPublishWorkflowAsync(p));
    R("qcFixAndVerifyWorkflow", "Workflow", W_, (p, ct) => WorkflowCommands.QcFixAndVerifyWorkflowAsync(p));
    R("newDrawing", "Drawing", W_, (p, ct) => DrawingCommands.NewDrawingAsync(p));
    R("documentsList", "Document", R_, (p, ct) => DocumentCommands.ListDocumentsAsync(p));
    R("documentOpen", "Document", W_, (p, ct) => DocumentCommands.OpenDocumentAsync(p));
    R("dxfToDwg", "Document", W_, (p, ct) => DocumentCommands.DxfToDwgAsync(p));
    R("documentActivate", "Document", W_, (p, ct) => DocumentCommands.ActivateDocumentAsync(p));
    R("documentClose", "Document", W_, (p, ct) => DocumentCommands.CloseDocumentAsync(p));
    R("drawingTransform", "DrawingTransform", W_, (p, ct) => DrawingTransformCommands.TransformDrawingAsync(p));
    R("saveDrawing", "Drawing", W_, (p, ct) => DrawingCommands.SaveDrawingAsync(p));
    R("undoDrawing", "Drawing", W_, (p, ct) => DrawingCommands.UndoDrawingAsync(p));
    R("redoDrawing", "Drawing", W_, (p, ct) => DrawingCommands.RedoDrawingAsync(p));
    R("batch", "Batch", W_, (p, ct) => BatchCommands.RunAsync(p, ct));
    R("createPolyline", "Acad", W_, (p, ct) => AcadCommands.CreatePolylineAsync(p));
    R("createHatch", "Acad", W_, (p, ct) => AcadCommands.CreateHatchAsync(p));
    R("createCircle", "Acad", W_, (p, ct) => AcadCommands.CreateCircleAsync(p));
    R("createDimension", "Dimension", W_, (p, ct) => DimensionCommands.CreateDimensionAsync(p));
    R("setDimStyle", "Dimension", W_, (p, ct) => DimensionCommands.SetDimStyleAsync(p));
    R("createPoints", "Acad", W_, (p, ct) => AcadCommands.CreatePointsAsync(p));   // C130b
    R("exportDxf", "Acad", W_, (p, ct) => AcadCommands.ExportDxfAsync(p));   // C130
    R("zoomTo", "Acad", W_, (p, ct) => AcadCommands.ZoomToAsync(p));   // C131
    R("createText", "Acad", W_, (p, ct) => AcadCommands.CreateTextAsync(p));
    R("create3dPolyline", "Acad", W_, (p, ct) => AcadCommands.Create3dPolylineAsync(p));
    R("createMText", "Acad", W_, (p, ct) => AcadCommands.CreateMTextAsync(p));
    R("createLineSegment", "Acad", W_, (p, ct) => AcadCommands.CreateLineSegmentAsync(p));
    R("listCivilObjectTypes", "Drawing", R_, (p, ct) => DrawingCommands.ListCivilObjectTypesAsync());
    R("getDrawingLayerStats", "DrawingStats", R_, (p, ct) => DrawingStatsCommands.GetDrawingLayerStatsAsync(p));
    R("setObjectStyle", "Display", W_, (p, ct) => DisplayCommands.SetObjectStyleAsync(p));
    R("listLayers", "Display", R_, (p, ct) => DisplayCommands.ListLayersAsync(p));
    R("setLayers", "Display", W_, (p, ct) => DisplayCommands.SetLayersAsync(p));
    R("layersImport", "LayerStandard", W_, (p, ct) => LayerStandardCommands.ImportLayersAsync(p));
    R("linetypesLoad", "LayerStandard", W_, (p, ct) => LayerStandardCommands.LoadLinetypesAsync(p));
    R("objectLayersGet", "LayerStandard", R_, (p, ct) => LayerStandardCommands.GetObjectLayersAsync(p));
    R("objectLayersSet", "LayerStandard", W_, (p, ct) => LayerStandardCommands.SetObjectLayersAsync(p));
    R("layerStatesList", "LayerStandard", R_, (p, ct) => LayerStandardCommands.ListLayerStatesAsync(p));
    R("layerStateSave", "LayerStandard", W_, (p, ct) => LayerStandardCommands.SaveLayerStateAsync(p));
    R("layerStateRestore", "LayerStandard", W_, (p, ct) => LayerStandardCommands.RestoreLayerStateAsync(p));
    R("layerStateUpdate", "LayerStandard", W_, (p, ct) => LayerStateUpdateCommands.UpdateLayerStateAsync(p));   // C236
    R("layerStateDelete", "LayerStandard", W_, (p, ct) => LayerStandardCommands.DeleteLayerStateAsync(p));
    R("layersPurge", "LayerStandard", W_, (p, ct) => LayerStandardCommands.PurgeLayersAsync(p));
    R("gridCrossesAdd", "GridCross", W_, (p, ct) => GridCrossCommands.AddGridCrossesAsync(p));
    R("northArrowAlign", "GridCross", W_, (p, ct) => GridCrossCommands.AlignNorthArrowAsync(p));
    R("structureBendAngles", "BendAngle", W_, (p, ct) => BendAngleCommands.StructureBendAnglesAsync(p));
    R("editProfileViewBands", "ProfileViewBandLabel", W_, (p, ct) => ProfileViewBandLabelCommands.EditProfileViewBandsAsync(p));
    R("clearProfileViewLabels", "ProfileViewBandLabel", W_, (p, ct) => ProfileViewBandLabelCommands.ClearProfileViewLabelsAsync(p));
    R("getScale", "Display", R_, (p, ct) => DisplayCommands.GetScaleAsync(p));
    R("setScale", "Display", W_, (p, ct) => DisplayCommands.SetScaleAsync(p));
    R("setCorridorFeatureLines", "Display", W_, (p, ct) => DisplayCommands.SetCorridorFeatureLinesAsync(p));
    R("listEntities", "EntityQuery", R_, (p, ct) => EntityQueryCommands.ListEntitiesAsync(p));
    R("setEntitiesLayer", "EntityQuery", W_, (p, ct) => EntityQueryCommands.SetEntitiesLayerAsync(p));
    R("networkClearance", "NetworkAnalysis", R_, (p, ct) => NetworkAnalysisCommands.NetworkClearanceAsync(p));
    R("networkQuantities", "NetworkAnalysis", R_, (p, ct) => NetworkAnalysisCommands.NetworkQuantitiesAsync(p));
    R("getPartStyle", "PartStyle", R_, (p, ct) => PartStyleCommands.GetPartStyleAsync(p));
    R("setPartStyle", "PartStyle", W_, (p, ct) => PartStyleCommands.SetPartStyleAsync(p));
    R("copyPartStyle", "PartStyle", W_, (p, ct) => PartStyleCommands.CopyPartStyleAsync(p));
    R("listLayouts", "Layout", R_, (p, ct) => LayoutCommands.ListLayoutsAsync(p));
    R("renameLayouts", "Layout", W_, (p, ct) => LayoutCommands.RenameLayoutsAsync(p));   // C124
    R("setLayoutOrder", "Layout", W_, (p, ct) => LayoutCommands.SetLayoutOrderAsync(p));   // C187
    R("replaceText", "Layout", W_, (p, ct) => LayoutCommands.ReplaceTextAsync(p));   // C124b
    R("setBlockAttributes", "Layout", W_, (p, ct) => LayoutCommands.SetBlockAttributesAsync(p));
    R("getBlockAttributes", "Layout", R_, (p, ct) => LayoutCommands.GetBlockAttributesAsync(p));
    R("addLayoutText", "Layout", W_, (p, ct) => LayoutCommands.AddLayoutTextAsync(p));
    R("addMLeader", "MLeader", W_, (p, ct) => MLeaderCommands.AddMLeaderAsync(p));
    R("eraseEntities", "EntityQuery", W_, (p, ct) => EntityQueryCommands.EraseEntitiesAsync(p));
    R("drawEntities", "EntityQuery", W_, (p, ct) => DrawEntitiesCommands.DrawEntitiesAsync(p, ct));   // C223
    R("addLayoutTable", "Layout", W_, (p, ct) => DrawEntitiesCommands.AddLayoutTableAsync(p, ct));   // C229
    R("addProfileViewGeometry", "ProfileViewPart", W_, (p, ct) => DrawEntitiesCommands.AddProfileViewGeometryAsync(p, ct));   // C235
    R("buildDetailSheet", "Layout", W_, (p, ct) => DetailSheetCommands.BuildDetailSheetAsync(p, ct));   // C239
    R("buildPlanProfileSheet", "Layout", W_, (p, ct) => PlanProfileSheetCommands.BuildPlanProfileSheetAsync(p, ct));   // C240
    R("placeFlags", "MLeader", W_, (p, ct) => PlaceFlagsCommands.PlaceFlagsAsync(p, ct));   // C241
    R("scaleEntities", "EntityQuery", W_, (p, ct) => EntityQueryCommands.ScaleEntitiesAsync(p));
    R("reverseCurves", "EntityQuery", W_, (p, ct) => EntityQueryCommands.ReverseCurvesAsync(p));   // C145
    R("moveMLeaderText", "EntityQuery", W_, (p, ct) => EntityQueryCommands.MoveMLeaderTextAsync(p));   // C146
    R("setEntitiesStyle", "EntityQuery", W_, (p, ct) => EntityQueryCommands.SetEntitiesStyleAsync(p));
    R("setDrawOrder", "EntityQuery", W_, (p, ct) => EntityQueryCommands.SetDrawOrderAsync(p));   // C177
    R("listLinetypes", "Display", R_, (p, ct) => DisplayCommands.ListLinetypesAsync(p));
    R("listTextStyles", "TextStyle", R_, (p, ct) => TextStyleCommands.ListTextStylesAsync(p));
    R("setTextStyle", "TextStyle", W_, (p, ct) => TextStyleCommands.SetTextStyleAsync(p));
    R("getBlockDefinition", "BlockDefinition", R_, (p, ct) => BlockDefinitionCommands.GetBlockDefinitionAsync(p));
    R("setBlockEntitiesColor", "BlockDefinition", W_, (p, ct) => BlockDefinitionCommands.SetBlockEntitiesColorAsync(p));
    R("editBlockEntities", "BlockGeometry", W_, (p, ct) => BlockGeometryCommands.EditBlockEntitiesAsync(p));
    R("replaceBlockReferences", "BlockGeometry", W_, (p, ct) => BlockGeometryCommands.ReplaceBlockReferencesAsync(p));
    R("setBlockDefinition", "BlockDefinition", W_, (p, ct) => BlockDefinitionCommands.SetBlockDefinitionAsync(p));
    R("reloadPlugin", "PluginReload", W_, (p, ct) => PluginReloadCommands.ReloadPluginAsync(p));
    R("listObjectClasses", "SaveDiagnostics", R_, (p, ct) => SaveDiagnosticsCommands.ListObjectClassesAsync(p));
    R("listLoadedModules", "SaveDiagnostics", R_, (p, ct) => SaveDiagnosticsCommands.ListLoadedModulesAsync(p));
    R("saveTest", "SaveDiagnostics", W_, (p, ct) => SaveDiagnosticsCommands.SaveTestAsync(p));
    R("replaceSolidsWithoutHistory", "SaveDiagnostics", W_, (p, ct) => SaveDiagnosticsCommands.ReplaceSolidsWithoutHistoryAsync(p));
    R("listPlotMedia", "PageSetup", R_, (p, ct) => PageSetupCommands.ListPlotMediaAsync(p));
    R("listPageSetups", "PageSetup", R_, (p, ct) => PageSetupCommands.ListPageSetupsAsync(p));
    R("setPageSetup", "PageSetup", W_, (p, ct) => PageSetupCommands.SetPageSetupAsync(p));
    R("applyPageSetup", "PageSetup", W_, (p, ct) => PageSetupCommands.ApplyPageSetupAsync(p));
    R("listExternalBlocks", "Block", W_, (p, ct) => BlockCommands.ListExternalBlocksAsync(p));
    R("importBlocks", "Block", W_, (p, ct) => BlockCommands.ImportBlocksAsync(p));
    R("insertBlock", "Block", W_, (p, ct) => BlockCommands.InsertBlockAsync(p));
    R("createLayout", "Layout", W_, (p, ct) => LayoutCommands.CreateLayoutAsync(p));
    R("deleteLayout", "Layout", W_, (p, ct) => LayoutCommands.DeleteLayoutAsync(p));
    R("setCurrentLayout", "Layout", W_, (p, ct) => LayoutCommands.SetCurrentLayoutAsync(p));   // C186
    R("createViewport", "Viewport", W_, (p, ct) => ViewportCommands.CreateViewportAsync(p));
    R("setViewport", "Viewport", W_, (p, ct) => ViewportCommands.SetViewportAsync(p));
    R("viewportLayerOverrides", "ViewportLayerOverride", W_, (p, ct) => ViewportLayerOverrideCommands.SetViewportLayerOverridesAsync(p));   // C99i
    R("getBlockReference", "BlockReference", R_, (p, ct) => BlockReferenceCommands.GetBlockReferenceAsync(p));
    R("listExternalStyles", "StyleTransfer", W_, (p, ct) => StyleTransferCommands.ListExternalStylesAsync(p));
    R("getAmbientSettings", "AmbientSettings", R_, (p, ct) => AmbientSettingsCommands.GetAmbientSettingsAsync(p));
    R("purgeBlocks", "Purge", W_, (p, ct) => PurgeCommands.PurgeBlocksAsync(p));
    R("setMLeaderStyle", "MLeader", W_, (p, ct) => MLeaderCommands.SetMLeaderStyleAsync(p));
    R("removePartsListFamily", "Purge", W_, (p, ct) => PurgeCommands.RemovePartsListFamilyAsync(p));
    R("setAmbientSettings", "AmbientSettings", W_, (p, ct) => AmbientSettingsCommands.SetAmbientSettingsAsync(p));
    R("civilSettingsList", "CivilSettings", R_, (p, ct) => CivilSettingsCommands.ListCivilSettingsAsync(p));
    R("civilSettingsGet", "CivilSettings", R_, (p, ct) => CivilSettingsCommands.GetCivilSettingsAsync(p));
    R("civilSettingsSet", "CivilSettings", W_, (p, ct) => CivilSettingsCommands.SetCivilSettingsAsync(p));
    R("importStyles", "StyleTransfer", W_, (p, ct) => StyleTransferCommands.ImportStylesAsync(p));
    R("setBlockReference", "BlockReference", W_, (p, ct) => BlockReferenceCommands.SetBlockReferenceAsync(p));
    R("eraseViewport", "Viewport", W_, (p, ct) => ViewportCommands.EraseViewportAsync(p));
    R("getSystemVariable", "SystemVariable", R_, (p, ct) => SystemVariableCommands.GetSystemVariableAsync(p));
    R("setSystemVariable", "SystemVariable", W_, (p, ct) => SystemVariableCommands.SetSystemVariableAsync(p));
    R("plotLayout", "Plot", W_, (p, ct) => PlotCommands.PlotLayoutAsync(p));
    R("setProfileViewRange", "ProfileViewRange", W_, (p, ct) => ProfileViewRangeCommands.SetProfileViewRangeAsync(p));
    R("listBandStyles", "BandStyle", R_, (p, ct) => BandStyleCommands.ListBandStylesAsync(p));
    R("listBandSets", "BandSet", R_, (p, ct) => BandSetCommands.ListBandSetsAsync(p));
    R("setBandSet", "BandSet", W_, (p, ct) => BandSetCommands.SetBandSetAsync(p));
    R("getBandStyle", "BandStyle", R_, (p, ct) => BandStyleCommands.GetBandStyleAsync(p));
    R("copyBandStyle", "BandStyle", W_, (p, ct) => BandStyleCommands.CopyBandStyleAsync(p));
    R("setBandStyle", "BandStyle", W_, (p, ct) => BandStyleCommands.SetBandStyleAsync(p));
    R("bandStyleTextSet", "BandStyle", W_, (p, ct) => BandStyleCommands.SetBandStyleTextsAsync(p));
    R("labelStylesList", "LabelStyleText", R_, (p, ct) => LabelStyleTextCommands.ListLabelStylesAsync(p));
    R("labelStyleTextGet", "LabelStyleText", R_, (p, ct) => LabelStyleTextCommands.GetLabelStyleTextAsync(p));
    R("labelStyleTextSet", "LabelStyleText", W_, (p, ct) => LabelStyleTextCommands.SetLabelStyleTextAsync(p));
    R("labelSetStyle", "LabelStyleAssign", W_, (p, ct) => LabelStyleAssignCommands.SetLabelStyleAsync(p));   // C120
    R("labelStyleCopy", "LabelStyleText", W_, (p, ct) => LabelStyleTextCommands.CopyLabelStyleAsync(p));
    R("profileViewAxisGet", "LabelStyleText", R_, (p, ct) => LabelStyleTextCommands.GetProfileViewAxesAsync(p));
    R("profileViewAxisSet", "LabelStyleText", W_, (p, ct) => LabelStyleTextCommands.SetProfileViewAxisAsync(p));
    R("labelSetsList", "LabelStyleProperty", R_, (p, ct) => LabelStylePropertyCommands.ListLabelSetsAsync(p));
    R("labelStylePropertiesGet", "LabelStyleProperty", R_, (p, ct) => LabelStylePropertyCommands.GetLabelStylePropertiesAsync(p));
    R("labelStylePropertiesSet", "LabelStyleProperty", W_, (p, ct) => LabelStylePropertyCommands.SetLabelStylePropertiesAsync(p));
    R("labelExpressionsList", "LabelExpression", R_, (p, ct) => LabelExpressionCommands.ListLabelExpressionsAsync(p));
    R("labelExpressionSet", "LabelExpression", W_, (p, ct) => LabelExpressionCommands.SetLabelExpressionAsync(p));
    R("labelExpressionDelete", "LabelExpression", W_, (p, ct) => LabelExpressionCommands.DeleteLabelExpressionAsync(p));
    R("styleDisplayGet", "StyleDisplay", R_, (p, ct) => StyleDisplayCommands.GetStyleDisplayAsync(p));
    R("styleDisplaySet", "StyleDisplay", W_, (p, ct) => StyleDisplayCommands.SetStyleDisplayAsync(p));
    R("getSelectedCivilObjectsInfo", "Drawing", R_, (p, ct) => DrawingCommands.GetSelectedCivilObjectsInfoAsync(p));
    R("getJobStatus", "Job", R_, (p, ct) => Task.FromResult<object?>(JobCommands.GetJobStatus(p)));
    R("cancelJob", "Job", W_, (p, ct) => Task.FromResult<object?>(JobCommands.CancelJob(p)));
    R("startJob", "Job", W_, (p, ct) => JobCommands.StartJobAsync(p));
    R("listCogoPoints", "Point", R_, (p, ct) => PointCommands.ListCogoPointsAsync(p));
    R("getCogoPoint", "Point", R_, (p, ct) => PointCommands.GetCogoPointAsync(p));
    R("setCogoPointsLayer", "Point", W_, (p, ct) => PointCommands.SetCogoPointsLayerAsync(p));   // C134
    R("createCogoPoints", "Point", W_, (p, ct) => PointCommands.CreateCogoPointsAsync(p));
    R("listPointGroups", "Point", R_, (p, ct) => PointCommands.ListPointGroupsAsync());
    R("importCogoPoints", "Point", W_, (p, ct) => PointCommands.ImportCogoPointsAsync(p));
    R("deleteCogoPoints", "Point", W_, (p, ct) => PointCommands.DeleteCogoPointsAsync(p));
    R("listAlignments", "Alignment", R_, (p, ct) => AlignmentCommands.ListAlignmentsAsync());
    R("getAlignment", "Alignment", R_, (p, ct) => AlignmentCommands.GetAlignmentAsync(p));
    R("alignmentStationToPoint", "Alignment", R_, (p, ct) => AlignmentCommands.StationToPointAsync(p));
    R("alignmentSampleStations", "Alignment", R_, (p, ct) => AlignmentCommands.SampleStationsAsync(p));
    R("alignmentPointToStation", "Alignment", R_, (p, ct) => AlignmentCommands.PointToStationAsync(p));
    R("alignmentEntitiesGet", "Alignment", R_, (p, ct) => AlignmentCommands.GetAlignmentEntitiesAsync(p));
    R("createAlignment", "Alignment", W_, (p, ct) => AlignmentCommands.CreateAlignmentAsync(p));
    R("deleteAlignment", "Alignment", W_, (p, ct) => AlignmentCommands.DeleteAlignmentAsync(p));
    R("listProfiles", "Profile", R_, (p, ct) => ProfileCommands.ListProfilesAsync(p));
    R("getProfile", "Profile", R_, (p, ct) => ProfileCommands.GetProfileAsync(p));
    R("getProfileElevation", "Profile", R_, (p, ct) => ProfileCommands.GetProfileElevationAsync(p));
    R("sampleProfileElevations", "Profile", R_, (p, ct) => ProfileCommands.SampleProfileElevationsAsync(p));
    R("createProfileFromSurface", "Profile", W_, (p, ct) => ProfileCommands.CreateProfileFromSurfaceAsync(p));
    R("createLayoutProfile", "Profile", W_, (p, ct) => ProfileCommands.CreateLayoutProfileAsync(p));
    R("deleteProfile", "Profile", W_, (p, ct) => ProfileCommands.DeleteProfileAsync(p));
    R("listSurfaces", "Surface", R_, (p, ct) => SurfaceCommands.ListSurfacesAsync());
    R("getSurface", "Surface", R_, (p, ct) => SurfaceCommands.GetSurfaceAsync(p));
    R("getSurfaceElevation", "Surface", R_, (p, ct) => SurfaceCommands.GetSurfaceElevationAsync(p));
    R("getSurfaceElevationsAlong", "Surface", R_, (p, ct) => SurfaceCommands.GetSurfaceElevationsAlongAsync(p));
    R("getSurfaceStatistics", "Surface", R_, (p, ct) => SurfaceCommands.GetSurfaceStatisticsAsync(p));
    R("listAssemblies", "AssemblyCreation", R_, (p, ct) => AssemblyCreationCommands.ListAssembliesAsync());
    R("getAssembly", "AssemblyCreation", R_, (p, ct) => AssemblyCreationCommands.GetAssemblyAsync(p));
    R("createSurface", "Surface", W_, (p, ct) => SurfaceCommands.CreateSurfaceAsync(p));
    R("deleteSurface", "Surface", W_, (p, ct) => SurfaceCommands.DeleteSurfaceAsync(p));
    R("renameSurface", "Surface", W_, (p, ct) => SurfaceCommands.RenameSurfaceAsync(p));   // C167
    R("addSurfacePoints", "Surface", W_, (p, ct) => SurfaceCommands.AddSurfacePointsAsync(p));
    R("addSurfaceBreakline", "Surface", W_, (p, ct) => SurfaceCommands.AddSurfaceBreaklineAsync(p));
    R("addSurfaceBoundary", "Surface", W_, (p, ct) => SurfaceCommands.AddSurfaceBoundaryAsync(p));
    R("pasteSurface", "SurfaceEdit", W_, (p, ct) => SurfaceEditCommands.PasteSurfaceAsync(p));
    R("simplifySurface", "SurfaceEdit", W_, (p, ct) => SurfaceEditCommands.SimplifySurfaceAsync(p));
    R("setSurfaceBuildOptions", "SurfaceEdit", W_, (p, ct) => SurfaceEditCommands.SetSurfaceBuildOptionsAsync(p));
    R("findSurfaceGaps", "SurfaceEdit", R_, (p, ct) => SurfaceEditCommands.FindSurfaceGapsAsync(p));
    R("extractSurfaceContours", "Surface", W_, (p, ct) => SurfaceCommands.ExtractSurfaceContoursAsync(p));
    R("exportLandXml", "LandXml", W_, (p, ct) => LandXmlCommands.ExportLandXmlAsync(p));
    R("getDataShortcutProject", "DataShortcutProject", R_, (p, ct) => DataShortcutProjectCommands.GetDataShortcutProjectAsync(p));
    R("setDataShortcutProject", "DataShortcutProject", W_, (p, ct) => DataShortcutProjectCommands.SetDataShortcutProjectAsync(p));
    R("publishDataShortcuts", "DataShortcutProject", W_, (p, ct) => DataShortcutProjectCommands.PublishDataShortcutsAsync(p));
    R("listExternalReferences", "ExternalReference", R_, (p, ct) => ExternalReferenceCommands.ListExternalReferencesAsync(p));
    R("setExternalReferencePath", "ExternalReference", W_, (p, ct) => ExternalReferenceCommands.SetExternalReferencePathAsync(p));
    R("xrefsList", "ExternalReference", R_, (p, ct) => ExternalReferenceCommands.ListXrefsAsync(p));
    R("xrefAttach", "ExternalReference", W_, (p, ct) => ExternalReferenceCommands.AttachXrefAsync(p));
    R("rasterAttach", "RasterImage", W_, (p, ct) => RasterImageCommands.AttachRasterAsync(p));   // C99h
    R("rasterDetach", "RasterImage", W_, (p, ct) => RasterImageCommands.DetachRasterAsync(p));   // C109
    R("xrefDetach", "ExternalReference", W_, (p, ct) => ExternalReferenceCommands.DetachXrefAsync(p));
    R("xrefUnload", "ExternalReference", W_, (p, ct) => ExternalReferenceCommands.UnloadXrefAsync(p));
    R("xrefReload", "ExternalReference", W_, (p, ct) => ExternalReferenceCommands.ReloadXrefAsync(p));
    R("xrefClip", "ExternalReference", W_, (p, ct) => ExternalReferenceCommands.ClipXrefAsync(p));   // C128
    R("computeSurfaceVolume", "Surface", R_, (p, ct) => SurfaceCommands.ComputeSurfaceVolumeAsync(p));
    R("addSurfaceWatershed", "Surface", W_, (p, ct) => SurfaceCommands.AddSurfaceWatershedsAsync(p));
    R("addSurfaceWatersheds", "Surface", W_, (p, ct) => SurfaceCommands.AddSurfaceWatershedsAsync(p));
    R("calculateSurfaceVolume", "Surface", R_, (p, ct) => SurfaceCommands.CalculateSurfaceVolumeAsync(p));
    R("getSurfaceVolumeReport", "Surface", R_, (p, ct) => SurfaceCommands.GetSurfaceVolumeReportAsync(p));
    R("calculateSurfaceVolumeByRegion", "Surface", R_, (p, ct) => SurfaceCommands.CalculateSurfaceVolumeByRegionAsync(p));
    R("analyzeSurfaceSlope", "Surface", R_, (p, ct) => SurfaceCommands.AnalyzeSurfaceSlopeAsync(p));
    R("analyzeSurfaceElevation", "Surface", R_, (p, ct) => SurfaceCommands.AnalyzeSurfaceElevationAsync(p));
    R("analyzeSurfaceDirections", "Surface", R_, (p, ct) => SurfaceCommands.AnalyzeSurfaceDirectionsAsync(p));
    R("setSurfaceContourInterval", "Surface", W_, (p, ct) => SurfaceCommands.SetSurfaceContourIntervalAsync(p));
    R("getSurfaceStatisticsDetailed", "Surface", R_, (p, ct) => SurfaceCommands.GetSurfaceStatisticsDetailedAsync(p));
    R("sampleSurfaceElevations", "Surface", R_, (p, ct) => SurfaceCommands.SampleSurfaceElevationsAsync(p));
    R("createSurfaceFromDem", "Surface", W_, (p, ct) => SurfaceCommands.CreateSurfaceFromDemAsync(p));
    R("listSampleLineGroups", "Section", R_, (p, ct) => SectionCommands.ListSampleLineGroupsAsync(p));
    R("getSectionData", "Section", R_, (p, ct) => SectionCommands.GetSectionDataAsync(p));
    R("createSampleLines", "Section", W_, (p, ct) => SectionCommands.CreateSampleLinesAsync(p));
    R("createSectionViews", "Section", W_, (p, ct) => SectionCommands.CreateSectionViewsAsync(p));
    R("listSectionViews", "Section", R_, (p, ct) => SectionCommands.ListSectionViewsAsync(p));
    R("updateSectionViewStyles", "Section", W_, (p, ct) => SectionCommands.UpdateSectionViewStylesAsync(p));
    R("createSectionViewGroup", "Section", W_, (p, ct) => SectionCommands.CreateSectionViewGroupAsync(p));
    R("exportSectionData", "Section", W_, (p, ct) => SectionCommands.ExportSectionDataAsync(p));

    // Parcels
    R("createParcel", "ParcelEditing", W_, (p, ct) => ParcelEditingCommands.CreateParcelAsync(p));
    R("editParcel", "ParcelEditing", W_, (p, ct) => ParcelEditingCommands.EditParcelAsync(p));
    R("adjustParcelLotLine", "ParcelEditing", W_, (p, ct) => ParcelEditingCommands.AdjustParcelLotLineAsync(p));
    R("reportParcels", "ParcelEditing", R_, (p, ct) => ParcelEditingCommands.ReportParcelsAsync(p));
    R("listHydrologyCapabilities", "Hydrology", R_, (p, ct) => HydrologyCommands.ListHydrologyCapabilitiesAsync());
    R("traceHydrologyFlowPath", "Hydrology", R_, (p, ct) => HydrologyCommands.TraceFlowPathAsync(p));
    R("findHydrologyLowPoint", "Hydrology", R_, (p, ct) => HydrologyCommands.FindLowPointAsync(p));
    R("estimateHydrologyRunoff", "Hydrology", W_, (p, ct) => HydrologyCommands.EstimateRunoffAsync(p));
    R("delineateWatershed", "Hydrology", R_, (p, ct) => HydrologyCommands.DelineateWatershedAsync(p));
    R("calculateCatchmentArea", "Hydrology", R_, (p, ct) => HydrologyCommands.CalculateCatchmentAreaAsync(p));
    R("watershedRunoffWorkflow", "Hydrology", W_, (p, ct) => HydrologyCommands.WatershedRunoffWorkflowAsync(p));
    R("runoffDetentionWorkflow", "Hydrology", W_, (p, ct) => HydrologyCommands.RunoffDetentionWorkflowAsync(p));
    R("runoffPipeWorkflow", "Hydrology", W_, (p, ct) => HydrologyCommands.RunoffPipeWorkflowAsync(p));
    R("listCorridors", "Corridor", R_, (p, ct) => CorridorCommands.ListCorridorsAsync());
    R("getCorridor", "Corridor", R_, (p, ct) => CorridorCommands.GetCorridorAsync(p));
    R("rebuildCorridor", "Corridor", W_, (p, ct) => CorridorCommands.RebuildCorridorAsync(p));
    R("getCorridorSurfaces", "Corridor", R_, (p, ct) => CorridorCommands.GetCorridorSurfacesAsync(p));
    R("getCorridorFeatureLines", "Corridor", R_, (p, ct) => CorridorCommands.GetCorridorFeatureLinesAsync(p));
    R("computeCorridorVolumes", "Corridor", R_, (p, ct) => CorridorCommands.ComputeCorridorVolumesAsync(p));
    R("getCorridorTargetMappings", "CorridorEditing", R_, (p, ct) => CorridorEditingCommands.GetCorridorTargetMappingsAsync(p));
    R("setCorridorTargetMappings", "CorridorEditing", W_, (p, ct) => CorridorEditingCommands.SetCorridorTargetMappingsAsync(p));
    R("addCorridorRegion", "CorridorEditing", W_, (p, ct) => CorridorEditingCommands.AddCorridorRegionAsync(p));
    R("editCorridorRegion", "CorridorEditing", W_, (p, ct) => CorridorEditingCommands.EditCorridorRegionAsync(p));
    R("splitCorridorRegion", "CorridorEditing", W_, (p, ct) => CorridorEditingCommands.SplitCorridorRegionAsync(p));
    R("deleteCorridorRegion", "CorridorEditing", W_, (p, ct) => CorridorEditingCommands.DeleteCorridorRegionAsync(p));
    R("createCorridor", "Corridor", W_, (p, ct) => CorridorCommands.CreateCorridorAsync(p));
    R("deleteCorridor", "Corridor", W_, (p, ct) => CorridorCommands.DeleteCorridorAsync(p));
    R("createCorridorSurface", "Corridor", W_, (p, ct) => CorridorCommands.CreateCorridorSurfaceAsync(p));
    R("addCorridorSurfaceBoundary", "Corridor", W_, (p, ct) => CorridorCommands.AddCorridorSurfaceBoundaryAsync(p));
    R("corridorQuantities", "CorridorQuantity", R_, (p, ct) => CorridorQuantityCommands.CorridorQuantitiesAsync(p));
    R("listIntersections", "Intersection", R_, (p, ct) => IntersectionCommands.ListIntersectionsAsync(p));
    R("createIntersection", "Intersection", W_, (p, ct) => IntersectionCommands.CreateIntersectionAsync(p));
    R("getIntersection", "Intersection", R_, (p, ct) => IntersectionCommands.GetIntersectionAsync(p));
    R("listParcels", "ParcelEditing", R_, (p, ct) => ParcelEditingCommands.ListParcelsAsync(p));
    R("getParcel", "ParcelEditing", R_, (p, ct) => ParcelEditingCommands.GetParcelAsync(p));
    R("listParcelSites", "ParcelEditing", R_, (p, ct) => ParcelEditingCommands.ListParcelSitesAsync());

    // Pipe networks (gravity)
    R("listPipeNetworks", "PipeNetwork", R_, (p, ct) => PipeNetworkCommands.ListPipeNetworksAsync());
    R("getPipeNetwork", "PipeNetwork", R_, (p, ct) => PipeNetworkCommands.GetPipeNetworkAsync(p));
    R("getPipe", "PipeNetwork", R_, (p, ct) => PipeNetworkCommands.GetPipeAsync(p));
    R("getStructure", "PipeNetwork", R_, (p, ct) => PipeNetworkCommands.GetStructureAsync(p));
    R("checkPipeNetworkInterference", "PipeNetwork", R_, (p, ct) => PipeNetworkCommands.CheckPipeNetworkInterferenceAsync(p));
    R("createPipeNetwork", "PipeNetwork", W_, (p, ct) => PipeNetworkCommands.CreatePipeNetworkAsync(p));
    R("addPipeToNetwork", "PipeNetwork", W_, (p, ct) => PipeNetworkCommands.AddPipeToNetworkAsync(p));
    R("resizePipeInNetwork", "PipeNetwork", W_, (p, ct) => PipeNetworkCommands.ResizePipeInNetworkAsync(p));
    R("setPipeEndElevations", "PipeNetwork", W_, (p, ct) => ProfileViewBandLabelCommands.ThenRefreshBandsAsync(PipeNetworkCommands.SetPipeEndElevationsAsync(p), p));   // C160
    R("syncPipeNetwork", "PipeNetwork", W_, (p, ct) => ProfileViewBandLabelCommands.ThenRefreshBandsAsync(PipeNetworkCommands.SyncPipeNetworkAsync(p), p));   // C191
    R("addServiceConnection", "PipeNetwork", W_, (p, ct) => ProfileViewBandLabelCommands.ThenRefreshBandsAsync(PipeNetworkCommands.AddServiceConnectionAsync(p), p));   // C192
    R("gradeGravityNetwork", "PipeNetwork", W_, (p, ct) => ProfileViewBandLabelCommands.ThenRefreshBandsAsync(PipeNetworkCommands.GradeGravityNetworkAsync(p), p));   // C193
    R("moveStructures", "PipeNetwork", W_, (p, ct) => ProfileViewBandLabelCommands.ThenRefreshBandsAsync(PipeNetworkCommands.MoveStructuresAsync(p), p));   // C194
    R("splitPipe", "PipeNetwork", W_, (p, ct) => ProfileViewBandLabelCommands.ThenRefreshBandsAsync(PipeNetworkCommands.SplitPipeAsync(p), p));   // C195
    R("splitBends", "PipeNetwork", W_, (p, ct) => ProfileViewBandLabelCommands.ThenRefreshBandsAsync(PipeNetworkCommands.SplitBendsAsync(p), p));   // C197
    R("pipeCoverAlong", "PipeNetwork", R_, (p, ct) => PipeNetworkCommands.PipeCoverAlongAsync(p));   // C196
    R("regenLayouts", "Layout", W_, (p, ct) => PipeNetworkCommands.RegenLayoutsAsync(p));   // C198
    R("linkNetworkPart", "PipeNetwork", W_, (p, ct) => PipeNetworkCommands.LinkNetworkPartAsync(p));   // C199
    R("getNetworkLinks", "PipeNetwork", R_, (p, ct) => PipeNetworkCommands.GetNetworkLinksAsync(p));   // C199
    R("networkCrossings", "PipeNetwork", R_, (p, ct) => PipeNetworkCommands.NetworkCrossingsAsync(p));   // C200
    R("networkFittings", "PipeNetwork", R_, (p, ct) => PipeNetworkCommands.NetworkFittingsAsync(p));   // C201
    R("applyRealSizeStructureStyles", "PipeNetwork", W_, (p, ct) => PipeNetworkCommands.ApplyRealSizeStructureStylesAsync(p));   // C202
    R("trimPipesToStructures", "PipeNetwork", W_, (p, ct) => ProfileViewBandLabelCommands.ThenRefreshBandsAsync(PipeNetworkCommands.TrimPipesToStructuresAsync(p), p));   // C190
    R("setPartLock", "PipeNetwork", W_, (p, ct) => PipeNetworkCommands.SetPartLockAsync(p));   // C226
    R("getPartLocks", "PipeNetwork", R_, (p, ct) => PipeNetworkCommands.GetPartLocksAsync(p));   // C226
    R("verifyDrawing", "Qc", R_, (p, ct) => PipeNetworkCommands.VerifyDrawingAsync(p));   // C227
    R("listServiceConnections", "PipeNetwork", R_, (p, ct) => PipeNetworkCommands.ListServiceConnectionsAsync(p));   // C228
    R("moveNetworkNode", "PipeNetwork", W_, (p, ct) => PipeNetworkCommands.MoveNetworkNodeAsync(p));   // C231
    R("offsetPipeRun", "PipeNetwork", W_, (p, ct) => PipeNetworkCommands.OffsetPipeRunAsync(p, ct));   // C232
    R("resolveCrossings", "PipeNetwork", W_, (p, ct) => PipeNetworkCommands.ResolveCrossingsAsync(p, ct));   // C233
    R("checkNetworkAgainstEntities", "PipeNetwork", R_, (p, ct) => PipeNetworkCommands.CheckNetworkAgainstEntitiesAsync(p));   // C234
    R("tracePipeChain", "PipeNetwork", R_, (p, ct) => PipeNetworkCommands.TracePipeChainAsync(p));   // C237
    R("applyChainProfile", "PipeNetwork", W_, (p, ct) => PipeNetworkCommands.ApplyChainProfileAsync(p, ct));   // C238
    R("setStructureSize", "PipeNetwork", W_, (p, ct) => PipeNetworkCommands.SetStructureSizeAsync(p));   // C172
    R("connectPipesToStructures", "PipeNetwork", W_, (p, ct) => PipeNetworkCommands.ConnectPipesToStructuresAsync(p));   // C176
    R("setNetworkSurface", "PipeNetwork", W_, (p, ct) => ProfileViewBandLabelCommands.ThenRefreshBandsAsync(PipeNetworkCommands.SetNetworkSurfaceAsync(p), p));   // C166
    R("setStructureRims", "PipeNetwork", W_, (p, ct) => ProfileViewBandLabelCommands.ThenRefreshBandsAsync(PipeNetworkCommands.SetStructureRimsAsync(p), p));   // C123, C160
    R("erasePipeNetworkParts", "PipeNetwork", W_, (p, ct) => ProfileViewBandLabelCommands.ThenRefreshBandsAsync(PipeNetworkCommands.ErasePipeNetworkPartsAsync(p), p));   // C125, C160
    R("renameNetworkPart", "PipeNetwork", W_, (p, ct) => PipeNetworkCommands.RenameNetworkPartAsync(p));
    R("addStructureToNetwork", "PipeNetwork", W_, (p, ct) => PipeNetworkCommands.AddStructureToNetworkAsync(p));
    R("listPipePartsCatalog", "PipeNetwork", R_, (p, ct) => PipeNetworkCommands.ListPipePartsCatalogAsync(p));
    R("addNetworkToProfileView", "PipeNetwork", W_, (p, ct) => PipeNetworkCommands.AddNetworkToProfileViewAsync(p));
    R("buildPipeNetworkRun", "PipeNetwork", W_, (p, ct) => PipeNetworkCommands.BuildPipeNetworkRunAsync(p));
    R("setStructureRotation", "StructureRotation", W_, (p, ct) => StructureRotationCommands.SetStructureRotationAsync(p));
    R("deletePipeNetwork", "PipeNetwork", W_, (p, ct) => PipeNetworkCommands.DeletePipeNetworkAsync(p));
    R("listCatalogPartFamilies", "PipeNetwork", R_, (p, ct) => PipeNetworkCommands.ListCatalogPartFamiliesAsync(p));
    R("addPartsListFamily", "PipeNetwork", W_, (p, ct) => PipeNetworkCommands.AddPartsListFamilyAsync(p));
    R("addPartsListSizes", "PipeNetwork", W_, (p, ct) => PipeNetworkCommands.AddPartsListSizesAsync(p));
    R("partsListSizeDataGet", "PartsListSizeData", R_, (p, ct) => PartsListSizeDataCommands.GetSizeDataAsync(p));
    R("partsListSizeDataSet", "PartsListSizeData", W_, (p, ct) => PartsListSizeDataCommands.SetSizeDataAsync(p));
    R("partDataSet", "PartsListSizeData", W_, (p, ct) => PartsListSizeDataCommands.SetPartDataAsync(p));   // C148
    R("setPartsListFamilyStyle", "NetworkPresentation", W_, (p, ct) => NetworkPresentationCommands.SetPartsListFamilyStyleAsync(p));
    R("addNetworkLabels", "NetworkPresentation", W_, (p, ct) => NetworkPresentationCommands.AddNetworkLabelsAsync(p));
    R("deleteNetworkLabels", "NetworkPresentation", W_, (p, ct) => NetworkPresentationCommands.DeleteNetworkLabelsAsync(p));
    R("listPressureNetworks", "PressureNetwork", R_, (p, ct) => PressureNetworkCommands.ListPressureNetworksAsync());
    R("getPressureNetworkInfo", "PressureNetwork", R_, (p, ct) => PressureNetworkCommands.GetPressureNetworkInfoAsync(p));
    R("createPressureNetwork", "PressureNetwork", W_, (p, ct) => PressureNetworkCommands.CreatePressureNetworkAsync(p));
    R("deletePressureNetwork", "PressureNetwork", W_, (p, ct) => PressureNetworkCommands.DeletePressureNetworkAsync(p));
    R("assignPressurePartsList", "PressureNetwork", W_, (p, ct) => PressureNetworkCommands.AssignPressurePartsListAsync(p));
    R("setPressureNetworkCover", "PressureNetwork", W_, (p, ct) => PressureNetworkCommands.SetPressureNetworkCoverAsync(p));
    R("validatePressureNetwork", "PressureNetwork", W_, (p, ct) => PressureNetworkCommands.ValidatePressureNetworkAsync(p));
    R("exportPressureNetwork", "PressureNetwork", W_, (p, ct) => PressureNetworkCommands.ExportPressureNetworkAsync(p));
    R("connectPressureNetworks", "PressureNetwork", W_, (p, ct) => PressureNetworkCommands.ConnectPressureNetworksAsync(p));
    R("addPressurePipe", "PressureNetwork", W_, (p, ct) => PressureNetworkCommands.AddPressurePipeAsync(p));
    R("getPressurePipeProperties", "PressureNetwork", R_, (p, ct) => PressureNetworkCommands.GetPressurePipePropertiesAsync(p));
    R("resizePressurePipe", "PressureNetwork", W_, (p, ct) => PressureNetworkCommands.ResizePressurePipeAsync(p));
    R("addPressureFitting", "PressureNetwork", W_, (p, ct) => PressureNetworkCommands.AddPressureFittingAsync(p));
    R("getPressureFittingProperties", "PressureNetwork", R_, (p, ct) => PressureNetworkCommands.GetPressureFittingPropertiesAsync(p));
    R("addPressureAppurtenance", "PressureNetwork", W_, (p, ct) => PressureNetworkCommands.AddPressureAppurtenanceAsync(p));

    // Grading
    R("listGradingGroups", "Grading", R_, (p, ct) => GradingCommands.ListGradingGroupsAsync());
    R("getGradingGroup", "Grading", R_, (p, ct) => GradingCommands.GetGradingGroupAsync(p));
    R("createGradingGroup", "Grading", W_, (p, ct) => GradingCommands.CreateGradingGroupAsync(p));
    R("deleteGradingGroup", "Grading", W_, (p, ct) => GradingCommands.DeleteGradingGroupAsync(p));
    R("getGradingGroupVolume", "Grading", R_, (p, ct) => GradingCommands.GetGradingGroupVolumeAsync(p));
    R("createSurfaceFromGradingGroup", "Grading", W_, (p, ct) => GradingCommands.CreateSurfaceFromGradingGroupAsync(p));
    R("listGradings", "Grading", R_, (p, ct) => GradingCommands.ListGradingsAsync(p));
    R("getGrading", "Grading", R_, (p, ct) => GradingCommands.GetGradingAsync(p));
    R("createGrading", "Grading", W_, (p, ct) => GradingCommands.CreateGradingAsync(p));
    R("deleteGrading", "Grading", W_, (p, ct) => GradingCommands.DeleteGradingAsync(p));
    R("listGradingCriteria", "Grading", R_, (p, ct) => GradingCommands.ListGradingCriteriaAsync());
    R("listFeatureLines", "Grading", R_, (p, ct) => GradingCommands.ListFeatureLinesAsync());
    R("getFeatureLine", "Grading", R_, (p, ct) => GradingCommands.GetFeatureLineAsync(p));
    R("exportFeatureLineAsPolyline", "Grading", W_, (p, ct) => GradingCommands.ExportFeatureLineAsPolylineAsync(p));
    R("createFeatureLine", "Grading", W_, (p, ct) => GradingCommands.CreateFeatureLineAsync(p));

    // Project / data shortcuts
    R("listDataShortcuts", "DataShortcut", R_, (p, ct) => DataShortcutCommands.ListDataShortcutsAsync());
    R("createDataShortcut", "DataShortcut", W_, (p, ct) => DataShortcutCommands.CreateDataShortcutAsync(p));
    R("promoteDataShortcut", "DataShortcut", W_, (p, ct) => DataShortcutCommands.PromoteDataShortcutAsync(p));
    R("referenceDataShortcut", "DataShortcut", R_, (p, ct) => DataShortcutCommands.ReferenceDataShortcutAsync(p));
    R("syncDataShortcuts", "DataShortcut", W_, (p, ct) => DataShortcutCommands.SyncDataShortcutsAsync(p));
    R("createDataShortcutReference", "DataShortcut", W_, (p, ct) => DataShortcutCommands.CreateDataShortcutReferenceAsync(p));

    // Point groups
    R("createPointGroup", "PointGroup", W_, (p, ct) => PointGroupCommands.CreatePointGroupAsync(p));
    R("updatePointGroup", "PointGroup", W_, (p, ct) => PointGroupCommands.UpdatePointGroupAsync(p));
    R("deletePointGroup", "PointGroup", W_, (p, ct) => PointGroupCommands.DeletePointGroupAsync(p));
    R("exportCogoPoints", "PointGroup", W_, (p, ct) => PointGroupCommands.ExportCogoPointsAsync(p));
    R("transformCogoPoints", "PointGroup", W_, (p, ct) => PointGroupCommands.TransformCogoPointsAsync(p));

    // COGO
    R("cogoInverse", "Cogo", W_, (p, ct) => CogoCommands.CogoInverseAsync(p));
    R("cogoDirectionDistance", "Cogo", W_, (p, ct) => CogoCommands.CogoDirectionDistanceAsync(p));
    R("cogoTraverse", "Cogo", W_, (p, ct) => CogoCommands.CogoTraverseAsync(p));
    R("cogoCurveSolve", "Cogo", W_, (p, ct) => CogoCommands.CogoCurveSolveAsync(p));
    R("listSurveyDatabases", "Cogo", R_, (p, ct) => CogoCommands.ListSurveyDatabasesAsync());
    R("createSurveyDatabase", "Cogo", W_, (p, ct) => CogoCommands.CreateSurveyDatabaseAsync(p));
    R("listSurveyFigures", "Cogo", R_, (p, ct) => CogoCommands.ListSurveyFiguresAsync(p));
    R("getSurveyFigure", "Cogo", R_, (p, ct) => CogoCommands.GetSurveyFigureAsync(p));
    R("listSurveyObservations", "Cogo", R_, (p, ct) => CogoCommands.ListSurveyObservationsAsync(p));
    R("adjustSurveyNetwork", "Cogo", W_, (p, ct) => CogoCommands.AdjustSurveyNetworkAsync(p));
    R("createSurveyFigure", "Cogo", W_, (p, ct) => CogoCommands.CreateSurveyFigureAsync(p));
    R("importSurveyLandXml", "Cogo", W_, (p, ct) => CogoCommands.ImportSurveyLandXmlAsync(p));

    // Alignment editing
    R("alignmentAddTangent", "AlignmentEdit", W_, (p, ct) => AlignmentEditCommands.AddTangentAsync(p));
    R("alignmentMoveVertex", "AlignmentEdit", W_, (p, ct) => AlignmentEditCommands.MoveVertexAsync(p));
    R("alignmentRemoveVertex", "AlignmentEdit", W_, (p, ct) => AlignmentEditCommands.RemoveVertexAsync(p));   // C119
    R("alignmentInsertVertex", "AlignmentEdit", W_, (p, ct) => AlignmentEditCommands.InsertVertexAsync(p));   // C169
    R("alignmentLabelSet", "AlignmentEdit", W_, (p, ct) => AlignmentEditCommands.SetLabelSetAsync(p));   // C175
    R("alignmentAddCurve", "AlignmentEdit", W_, (p, ct) => AlignmentEditCommands.AddCurveAsync(p));
    R("alignmentAddSpiral", "AlignmentEdit", W_, (p, ct) => AlignmentEditCommands.AddSpiralAsync(p));
    R("alignmentDeleteEntity", "AlignmentEdit", W_, (p, ct) => AlignmentEditCommands.DeleteEntityAsync(p));
    R("alignmentReplaceCurveWithLines", "AlignmentEdit", W_, (p, ct) => AlignmentEditCommands.ReplaceCurveWithLinesAsync(p));
    R("alignmentSetStationEquation", "AlignmentEdit", W_, (p, ct) => AlignmentEditCommands.SetStationEquationAsync(p));
    R("alignmentGetStationOffset", "AlignmentEdit", R_, (p, ct) => AlignmentEditCommands.GetStationOffsetAsync(p));
    R("alignmentOffsetCreate", "AlignmentEdit", W_, (p, ct) => AlignmentEditCommands.OffsetCreateAsync(p));
    R("alignmentWidenTransition", "AlignmentEdit", W_, (p, ct) => AlignmentEditCommands.WidenTransitionAsync(p));

    // Profile editing
    R("profileAddPvi", "ProfileEdit", W_, (p, ct) => ProfileEditCommands.AddPviAsync(p));
    R("profileDeletePvi", "ProfileEdit", W_, (p, ct) => ProfileEditCommands.DeletePviAsync(p));
    R("getProfileViewProfiles", "ProfileEdit", R_, (p, ct) => ProfileEditCommands.GetProfileViewProfilesAsync(p));
    R("setProfileViewProfiles", "ProfileEdit", W_, (p, ct) => ProfileEditCommands.SetProfileViewProfilesAsync(p));
    R("getProfileViewParts", "ProfileViewPart", R_, (p, ct) => ProfileViewPartCommands.GetProfileViewPartsAsync(p));
    R("plotViewPng", "Plot", W_, (p, ct) => PlotCommands.PlotViewPngAsync(p));
    R("setProfileViewParts", "ProfileViewPart", W_, (p, ct) => ProfileViewPartCommands.SetProfileViewPartsAsync(p));
    R("listProfileViewStyles", "ProfileViewPart", R_, (p, ct) => ProfileViewPartCommands.ListProfileViewStylesAsync(p));
    R("setProfileViewStyle", "ProfileViewPart", W_, (p, ct) => ProfileViewPartCommands.SetProfileViewStyleAsync(p));
    R("profileViewStyleGraphSet", "ProfileViewPart", W_, (p, ct) => ProfileViewPartCommands.SetProfileViewStyleGraphAsync(p));
    R("profileViewStationToXY", "ProfileViewPart", R_, (p, ct) => ProfileViewPartCommands.ProfileViewStationToXyAsync(p));
    R("profileViewMove", "ProfileViewPart", W_, (p, ct) => ProfileViewPartCommands.MoveProfileViewAsync(p));
    R("profileViewDelete", "ProfileViewPart", W_, (p, ct) => ProfileViewPartCommands.DeleteProfileViewAsync(p));   // C106
    R("getProfileViewBands", "ProfileViewBandLabel", R_, (p, ct) => ProfileViewBandLabelCommands.GetProfileViewBandsAsync(p));
    R("listProfileViewBandLabels", "BandLabel", R_, (p, ct) => BandLabelCommands.ListProfileViewBandLabelsAsync(p));
    R("setProfileViewBandLabels", "BandLabel", W_, (p, ct) => BandLabelCommands.SetProfileViewBandLabelsAsync(p));
    R("weedProfileViewBandLabels", "BandLabel", W_, (p, ct) => BandLabelCommands.WeedProfileViewBandLabelsAsync(p));   // C204
    R("listProfileViews", "ProfileViewBandLabel", R_, (p, ct) => ProfileViewBandLabelCommands.ListProfileViewsAsync(p));   // C122
    R("profileViewBandsRefresh", "ProfileViewBandLabel", W_, (p, ct) => ProfileViewBandLabelCommands.RefreshProfileViewBandsAsync(p));   // C160
    R("setProfileViewBands", "ProfileViewBandLabel", W_, (p, ct) => ProfileViewBandLabelCommands.SetProfileViewBandsAsync(p));
    R("listProfileLabelStyles", "ProfileViewBandLabel", R_, (p, ct) => ProfileViewBandLabelCommands.ListProfileLabelStylesAsync(p));
    R("addProfileLineLabels", "ProfileViewBandLabel", W_, (p, ct) => ProfileViewBandLabelCommands.AddProfileLineLabelsAsync(p));
    R("getProfileLabelStyle", "ProfileLabelStyle", R_, (p, ct) => ProfileLabelStyleCommands.GetProfileLabelStyleAsync(p));
    R("copyProfileLabelStyle", "ProfileLabelStyle", W_, (p, ct) => ProfileLabelStyleCommands.CopyProfileLabelStyleAsync(p));
    R("setProfileLabelStyleText", "ProfileLabelStyle", W_, (p, ct) => ProfileLabelStyleCommands.SetProfileLabelStyleTextAsync(p));
    R("profileAddCurve", "ProfileEdit", W_, (p, ct) => ProfileEditCommands.AddCurveAsync(p));
    R("profileSetGrade", "ProfileEdit", W_, (p, ct) => ProfileEditCommands.SetGradeAsync(p));
    R("profileGetElevation", "ProfileEdit", W_, (p, ct) => ProfileEditCommands.GetElevationAsync(p));
    R("profileCheckKValues", "ProfileEdit", R_, (p, ct) => ProfileEditCommands.CheckKValuesAsync(p));
    R("profileViewCreate", "ProfileEdit", W_, (p, ct) => ProfileEditCommands.ProfileViewCreateAsync(p));
    R("profileViewBandSet", "ProfileEdit", W_, (p, ct) => ProfileEditCommands.ProfileViewBandSetAsync(p));
    R("profileFromNetwork", "ProfileFromNetwork", W_, (p, ct) => ProfileFromNetworkCommands.ProfileFromNetworkAsync(p));   // C103
    R("profileViewApplyBandSet", "ProfileViewBandSetApply", W_, (p, ct) => ProfileViewBandSetApplyCommands.ApplyBandSetAsync(p));   // C104
    R("entityIdsSet", "ScriptId", W_, (p, ct) => ScriptIdCommands.SetAsync(p));   // C105
    R("entityIdsClear", "ScriptId", W_, (p, ct) => ScriptIdCommands.ClearAsync(p));   // C105
    R("moveEntities", "ScriptId", W_, (p, ct) => ScriptIdCommands.MoveAsync(p));   // C114
    R("setMLeaderMask", "MLeaderMask", W_, (p, ct) => MLeaderMaskCommands.SetMaskAsync(p));   // C117
    R("startCommand", "InteractiveCommand", W_, (p, ct) => InteractiveCommandCommands.StartAsync(p));   // C118
    R("exportToAutocad", "InteractiveCommand", W_, (p, ct) => InteractiveCommandCommands.ExportToAutocadAsync(p));   // C188

    // Plan production
    R("listSheetSets", "PlanProduction", R_, (p, ct) => PlanProductionCommands.ListSheetSetsAsync());
    R("getSheetSetInfo", "PlanProduction", R_, (p, ct) => PlanProductionCommands.GetSheetSetInfoAsync(p));
    R("createSheetSet", "PlanProduction", W_, (p, ct) => PlanProductionCommands.CreateSheetSetAsync(p));
    R("addSheet", "PlanProduction", W_, (p, ct) => PlanProductionCommands.AddSheetAsync(p));
    R("getSheetProperties", "PlanProduction", R_, (p, ct) => PlanProductionCommands.GetSheetPropertiesAsync(p));
    R("setSheetTitleBlock", "PlanProduction", W_, (p, ct) => PlanProductionCommands.SetSheetTitleBlockAsync(p));
    R("createPlanProfileSheet", "PlanProduction", W_, (p, ct) => PlanProductionCommands.CreatePlanProfileSheetAsync(p));
    R("updatePlanProfileSheetAlignment", "PlanProduction", W_, (p, ct) => PlanProductionCommands.UpdatePlanProfileSheetAlignmentAsync(p));
    R("createSheetView", "PlanProduction", W_, (p, ct) => PlanProductionCommands.CreateSheetViewAsync(p));
    R("setSheetViewScale", "PlanProduction", W_, (p, ct) => PlanProductionCommands.SetSheetViewScaleAsync(p));
    R("publishSheetPdf", "PlanProduction", W_, (p, ct) => PlanProductionCommands.PublishSheetPdfAsync(p));
    R("exportSheetSet", "PlanProduction", W_, (p, ct) => PlanProductionCommands.ExportSheetSetAsync(p));

    // QC checks
    R("qcCheckAlignment", "Qc", R_, (p, ct) => QcCommands.QcCheckAlignmentAsync(p));
    R("qcCheckProfile", "Qc", R_, (p, ct) => QcCommands.QcCheckProfileAsync(p));
    R("qcCheckCorridor", "Qc", R_, (p, ct) => QcCommands.QcCheckCorridorAsync(p));
    R("qcCheckPipeNetwork", "Qc", R_, (p, ct) => QcCommands.QcCheckPipeNetworkAsync(p));
    R("qcCheckSurface", "Qc", R_, (p, ct) => QcCommands.QcCheckSurfaceAsync(p));
    R("qcCheckLabels", "Qc", R_, (p, ct) => QcCommands.QcCheckLabelsAsync(p));
    R("qcReportGenerate", "Qc", R_, (p, ct) => QcCommands.QcReportGenerateAsync(p));
    R("qcCheckDrawingStandards", "Qc", R_, (p, ct) => QcCommands.QcCheckDrawingStandardsAsync(p));
    R("qcFixDrawingStandards", "Qc", W_, (p, ct) => QcCommands.QcFixDrawingStandardsAsync(p));

    // Quantity takeoff
    R("qtyCorridorVolumes", "Quantity", W_, (p, ct) => QuantityCommands.QtyCorridorVolumesAsync(p));
    R("qtySurfaceVolume", "Quantity", R_, (p, ct) => QuantityCommands.QtySurfaceVolumeAsync(p));
    R("qtyPipeNetworkLengths", "Quantity", R_, (p, ct) => QuantityCommands.QtyPipeNetworkLengthsAsync(p));
    R("qtyPressureNetworkLengths", "Quantity", R_, (p, ct) => QuantityCommands.QtyPressureNetworkLengthsAsync(p));
    R("qtyParcelAreas", "Quantity", R_, (p, ct) => QuantityCommands.QtyParcelAreasAsync(p));
    R("qtyAlignmentLengths", "Quantity", R_, (p, ct) => QuantityCommands.QtyAlignmentLengthsAsync(p));
    R("qtyPointCountByGroup", "Quantity", R_, (p, ct) => QuantityCommands.QtyPointCountByGroupAsync(p));
    R("qtyExportToCsv", "Quantity", R_, (p, ct) => QuantityCommands.QtyExportToCsvAsync(p));
    R("qtyMaterialListGet", "Quantity", W_, (p, ct) => QuantityCommands.QtyMaterialListGetAsync(p));
    R("qtyEarthworkSummary", "Quantity", R_, (p, ct) => QuantityCommands.QtyEarthworkSummaryAsync(p));

    // Pipe hydraulics
    R("calculatePipeNetworkHgl", "PipeHydraulics", R_, (p, ct) => PipeHydraulicsCommands.CalculatePipeNetworkHglAsync(p));
    R("analyzePipeNetworkHydraulics", "PipeHydraulics", R_, (p, ct) => PipeHydraulicsCommands.AnalyzePipeNetworkHydraulicsAsync(p));
    R("getPipeStructureProperties", "PipeHydraulics", R_, (p, ct) => PipeHydraulicsCommands.GetPipeStructurePropertiesAsync(p));

    // Superelevation tools
    R("getSuperelevation", "Superelevation", R_, (p, ct) => SuperelevationCommands.GetSuperelevationAsync(p));
    R("setSuperelevation", "Superelevation", W_, (p, ct) => SuperelevationCommands.SetSuperelevationAsync(p));
    R("checkSuperelevationDesign", "Superelevation", R_, (p, ct) => SuperelevationCommands.CheckSuperelevationDesignAsync(p));
    R("generateSuperelevationReport", "Superelevation", R_, (p, ct) => SuperelevationCommands.GenerateSuperelevationReportAsync(p));

    // Assembly creation
    R("createAssembly", "AssemblyCreation", W_, (p, ct) => AssemblyCreationCommands.CreateAssemblyAsync(p));
    R("createSubassembly", "AssemblyCreation", W_, (p, ct) => AssemblyCreationCommands.CreateSubassemblyAsync(p));
    R("editAssembly", "AssemblyCreation", W_, (p, ct) => AssemblyCreationCommands.EditAssemblyAsync(p));

    // Sight distance
    R("calculateSightDistance", "SightDistance", R_, (p, ct) => SightDistanceCommands.CalculateSightDistanceAsync(p));
    R("checkStoppingDistance", "SightDistance", R_, (p, ct) => SightDistanceCommands.CheckStoppingDistanceAsync(p));

    // Detention basin
    R("calculateDetentionBasinSize", "Detention", W_, (p, ct) => DetentionCommands.CalculateDetentionBasinSizeAsync(p));
    R("calculateDetentionStageStorage", "Detention", W_, (p, ct) => DetentionCommands.CalculateDetentionStageStorageAsync(p));

    // Slope analysis
    R("calculateSlopeGeometry", "SlopeAnalysis", R_, (p, ct) => SlopeAnalysisCommands.CalculateSlopeGeometryAsync(p));
    R("checkSlopeStability", "SlopeAnalysis", W_, (p, ct) => SlopeAnalysisCommands.CheckSlopeStabilityAsync(p));

    // Cost estimation
    R("exportPayItems", "CostEstimation", W_, (p, ct) => CostEstimationCommands.ExportPayItemsAsync(p));
    R("calculateMaterialCostEstimate", "CostEstimation", R_, (p, ct) => CostEstimationCommands.CalculateMaterialCostEstimateAsync(p));

    // Labels
    R("listLabelStyles", "Label", R_, (p, ct) => LabelCommands.ListLabelStylesAsync(p));
    R("listLabels", "Label", R_, (p, ct) => LabelCommands.ListLabelsAsync(p));
    R("addLabel", "Label", W_, (p, ct) => LabelCommands.AddLabelAsync(p));
    R("listStyles", "Style", R_, (p, ct) => StyleCommands.ListStylesAsync(p));
    R("getStyle", "Style", R_, (p, ct) => StyleCommands.GetStyleAsync(p));

    // Catchment management
    R("listCatchmentGroups", "Catchment", R_, (p, ct) => CatchmentCommands.ListCatchmentGroupsAsync());
    R("getCatchmentGroup", "Catchment", R_, (p, ct) => CatchmentCommands.GetCatchmentGroupAsync(p));
    R("listCatchments", "Catchment", R_, (p, ct) => CatchmentCommands.ListCatchmentsAsync());
    R("getCatchmentProperties", "Catchment", R_, (p, ct) => CatchmentCommands.GetCatchmentPropertiesAsync(p));
    R("setCatchmentProperties", "Catchment", W_, (p, ct) => CatchmentCommands.SetCatchmentPropertiesAsync(p));
    R("copyCatchmentToGroup", "Catchment", W_, (p, ct) => CatchmentCommands.CopyCatchmentToGroupAsync(p));
    R("getCatchmentFlowPath", "Catchment", R_, (p, ct) => CatchmentCommands.GetCatchmentFlowPathAsync(p));
    R("getCatchmentBoundary", "Catchment", R_, (p, ct) => CatchmentCommands.GetCatchmentBoundaryAsync(p));

    // Time of concentration & hydrograph
    R("calculateTimeOfConcentration", "TimeOfConcentration", W_, (p, ct) => TimeOfConcentrationCommands.CalculateTimeOfConcentrationAsync(p));
    R("generateHydrograph", "TimeOfConcentration", W_, (p, ct) => TimeOfConcentrationCommands.GenerateHydrographAsync(p));
    R("listTcMethods", "TimeOfConcentration", R_, (p, ct) => TimeOfConcentrationCommands.ListTcMethodsAsync());

    // Storm and Sanitary Analysis (STM)
    R("exportStm", "Stm", W_, (p, ct) => StmCommands.ExportStmAsync(p));
    R("importStm", "Stm", W_, (p, ct) => StmCommands.ImportStmAsync(p));
    R("openStormSanitaryAnalysis", "Stm", W_, (p, ct) => StmCommands.OpenStormSanitaryAnalysisAsync(p));
    R("listSsaCapabilities", "Stm", R_, (p, ct) => StmCommands.ListSsaCapabilitiesAsync());

    // C153: the table itself
    R("getRules", "Rules", R_, (p, ct) => RulesProfile.GetRulesAsync(p));   // C222
    R("listCommands", "Registry", R_, (p, ct) => Task.FromResult<object?>(Describe(p)));
    return all;
  }

  /// <summary>C153: the commands, optionally filtered by domain or a name fragment. C221: describe: true adds each
  /// command's parameters, answer fields, the change that last shaped them and a note (CommandDocs; params null for
  /// commands not described yet).</summary>
  public static Dictionary<string, object?> Describe(JsonObject? parameters)
  {
    var domain = PluginRuntime.GetOptionalString(parameters, "domain");
    var contains = PluginRuntime.GetOptionalString(parameters, "contains");
    var describe = PluginRuntime.GetOptionalBool(parameters, "describe") ?? false;
    var list = All.Values
      .Where(c => domain == null || string.Equals(c.Domain, domain, StringComparison.OrdinalIgnoreCase))
      .Where(c => contains == null || c.Name.Contains(contains, StringComparison.OrdinalIgnoreCase))
      .OrderBy(c => c.Domain, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal)
      .Select(c =>
      {
        var entry = new Dictionary<string, object?> { ["name"] = c.Name, ["domain"] = c.Domain, ["kind"] = c.Kind == CommandKind.Read ? "read" : "write" };
        if (describe)
        {
          var doc = CommandDocs.All.TryGetValue(c.Name, out var d) ? d : null;
          entry["params"] = doc?.Params;
          entry["returns"] = doc?.Returns;
          entry["since"] = doc?.Since;
          if (doc?.Note != null) entry["note"] = doc.Note;
        }
        return entry;
      })
      .ToList();
    var result = new Dictionary<string, object?> { ["count"] = list.Count, ["total"] = All.Count, ["commands"] = list };
    if (describe) result["described"] = list.Count(e => e["params"] != null);
    return result;
  }
}
