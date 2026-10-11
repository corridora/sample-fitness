targetScope = 'resourceGroup'
@description('All resources are sample-owned except an explicitly selected Cosmos account.')
param prefix string
param owner string
param location string = resourceGroup().location
param image string
param existingCosmosName string = ''
param cosmosFreeTier bool = true
param forwarding bool = false
param synthetics bool = false
param otlpEndpoint string
param rumScript string
param rumKey string
param serviceNamespace string
param deploymentEnvironment string
param dailyStart string = '14:00'
@minValue(1)
@maxValue(600)
param windowSeconds int = 600
param dailyTimer string = '0 1 14 * * *'
param watchdogTimer string = '0 20 14 * * *'
param recommenderUrl string = ''
@secure()
param ingestKey string
@secure()
param controlToken string
@secure()
param downstreamToken string = ''
var tags = { 'corridora-sample': 'fitness', 'corridora-owner': owner }
var suffix = uniqueString(resourceGroup().id, prefix)
var cosmosName = empty(existingCosmosName) ? '${prefix}-cosmos-${suffix}' : existingCosmosName
var cosmosDatabase = '${prefix}-fitness'
var storageName = 'fit${take(suffix, 18)}'
var diagnosticName = 'fitdiag${take(suffix, 17)}'
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = { name: '${prefix}-identity', location: location, tags: tags }
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: { minimumTlsVersion: 'TLS1_2', allowBlobPublicAccess: false, allowSharedKeyAccess: true, supportsHttpsTrafficOnly: true }
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = { parent: storage, name: 'default', properties: {} }
resource exports 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = { parent: blobs, name: 'exports', properties: { publicAccess: 'None' } }
resource packages 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = { parent: blobs, name: 'function-packages', properties: { publicAccess: 'None' } }
resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = { parent: storage, name: 'default', properties: {} }
resource summaryQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = { parent: queueService, name: 'weekly-summary' }
resource poisonQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = { parent: queueService, name: 'weekly-summary-poison' }
resource lifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: { policy: { rules: [{ name: 'expire-export-fixtures', enabled: true, type: 'Lifecycle', definition: { filters: { blobTypes: ['blockBlob'], prefixMatch: ['exports/'] }, actions: { baseBlob: { delete: { daysAfterModificationGreaterThan: 7 } } } } }] } }
}
resource newCosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = if (empty(existingCosmosName)) {
  name: cosmosName
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: { databaseAccountOfferType: 'Standard', enableFreeTier: cosmosFreeTier, disableLocalAuth: true, locations: [{ locationName: location, failoverPriority: 0 }], consistencyPolicy: { defaultConsistencyLevel: 'Session' } }
}
resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' existing = { name: cosmosName }
resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmos
  name: cosmosDatabase
  properties: { resource: { id: cosmosDatabase }, options: { throughput: 400 } }
  dependsOn: [newCosmos]
}
resource records 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: database
  name: 'records'
  properties: { resource: { id: 'records', partitionKey: { paths: ['/kind'], kind: 'Hash' }, defaultTtl: 604800 }, options: {} }
}
resource cosmosRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmos
  name: guid(cosmos.id, identity.id, cosmosDatabase)
  properties: { roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002', principalId: identity.properties.principalId, scope: '${cosmos.id}/dbs/${cosmosDatabase}' }
  dependsOn: [records]
}
resource storageRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in ['b7e6dc6d-f1e8-4753-8033-0f276bb0955b', '974c5e8b-45b9-4653-ba55-5f855dd0fb88']: {
  name: guid(storage.id, identity.id, role)
  scope: storage
  properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role) }
}]
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${prefix}-kv-${take(suffix, 8)}'
  location: location
  tags: tags
  properties: { tenantId: subscription().tenantId, sku: { family: 'A', name: 'standard' }, enableRbacAuthorization: true, enableSoftDelete: true, softDeleteRetentionInDays: 7, accessPolicies: [] }
}
resource ingestSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = { parent: vault, name: 'corridora-ingest', properties: { value: ingestKey } }
resource controlSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = { parent: vault, name: 'demo-control', properties: { value: controlToken } }
resource downstreamSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = { parent: vault, name: 'downstream-auth', properties: { value: empty(downstreamToken) ? 'disabled' : downstreamToken } }
resource vaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, identity.id)
  scope: vault
  properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6') }
}
resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${prefix}-env'
  location: location
  tags: tags
  properties: { appLogsConfiguration: { destination: forwarding ? 'azure-monitor' : 'none' }, workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }] }
}
var commonEnv = [
  { name: 'SAMPLE_MODE', value: 'cloud' }
  { name: 'PUBLIC_URL', value: 'https://${prefix}-api.${environment.properties.defaultDomain}' }
  { name: 'AZURE_API_RESOURCE_ID', value: resourceId('Microsoft.App/containerApps', '${prefix}-api') }
  { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
  { name: 'AZURE_STORAGE_ACCOUNT', value: storage.name }
  { name: 'AZURE_COSMOS_ENDPOINT', value: cosmos.properties.documentEndpoint }
  { name: 'AZURE_COSMOS_DATABASE', value: cosmosDatabase }
  { name: 'CORRIDORA_OTLP_ENDPOINT', value: otlpEndpoint }
  { name: 'CORRIDORA_RUM_KEY', value: rumKey }
  { name: 'CORRIDORA_RUM_SCRIPT_URL', value: rumScript }
  { name: 'OTEL_SERVICE_NAMESPACE', value: serviceNamespace }
  { name: 'DEPLOYMENT_ENVIRONMENT', value: deploymentEnvironment }
  { name: 'DEMO_DAILY_START_UTC', value: dailyStart }
  { name: 'DEMO_WINDOW_SECONDS', value: string(windowSeconds) }
  { name: 'RECOMMENDER_URL', value: recommenderUrl }
  { name: 'AZURE_LOCATION', value: location }
]
resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-api'
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  properties: {
    managedEnvironmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: { activeRevisionsMode: 'Single', ingress: { external: true, targetPort: 8080, transport: 'http', allowInsecure: false }, secrets: [
      { name: 'ingest', keyVaultUrl: ingestSecret.properties.secretUri, identity: identity.id }
      { name: 'control', keyVaultUrl: controlSecret.properties.secretUri, identity: identity.id }
      { name: 'downstream', keyVaultUrl: downstreamSecret.properties.secretUri, identity: identity.id }
    ] }
    template: { containers: [{ name: 'fitness-api', image: image, resources: { cpu: json('0.25'), memory: '0.5Gi' }, env: concat(commonEnv, [
      { name: 'PORT', value: '8080' }
      { name: 'CORRIDORA_INGEST_KEY', secretRef: 'ingest' }
      { name: 'DEMO_CONTROL_TOKEN', secretRef: 'control' }
      { name: 'DOWNSTREAM_AUTH_TOKEN', secretRef: 'downstream' }
    ]), probes: [{ type: 'Readiness', httpGet: { path: '/healthz', port: 8080 }, initialDelaySeconds: 10, periodSeconds: 10 }] }], scale: { minReplicas: 0, maxReplicas: 2, rules: [{ name: 'http', http: { metadata: { concurrentRequests: '10' } } }] } }
  }
  dependsOn: [vaultRole, cosmosRole, storageRoles]
}
resource flexPlan 'Microsoft.Web/serverfarms@2024-04-01' = { name: '${prefix}-flex', location: location, tags: tags, sku: { name: 'FC1', tier: 'FlexConsumption' }, properties: { reserved: true } }
resource worker 'Microsoft.Web/sites@2025-03-01' = {
  name: '${prefix}-worker-${take(suffix, 7)}'
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  properties: {
    serverFarmId: flexPlan.id
    httpsOnly: true
    keyVaultReferenceIdentity: identity.id
    functionAppConfig: { runtime: { name: 'dotnet-isolated', version: '10.0' }, deployment: { storage: { type: 'blobContainer', value: '${storage.properties.primaryEndpoints.blob}function-packages', authentication: { type: 'UserAssignedIdentity', userAssignedIdentityResourceId: identity.id } } }, scaleAndConcurrency: { maximumInstanceCount: 40, instanceMemoryMB: 512 } }
    siteConfig: { appSettings: concat(commonEnv, [
      { name: 'AzureWebJobsStorage__accountName', value: storage.name }
      { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
      { name: 'AzureWebJobsStorage__clientId', value: identity.properties.clientId }
      { name: 'CORRIDORA_INGEST_KEY', value: '@Microsoft.KeyVault(SecretUri=${ingestSecret.properties.secretUri})' }
      { name: 'DEMO_CONTROL_TOKEN', value: '@Microsoft.KeyVault(SecretUri=${controlSecret.properties.secretUri})' }
      { name: 'DOWNSTREAM_AUTH_TOKEN', value: '@Microsoft.KeyVault(SecretUri=${downstreamSecret.properties.secretUri})' }
      { name: 'ENABLE_PROVIDER_FORWARDING', value: string(forwarding) }
      { name: 'ENABLE_SYNTHETICS', value: string(synthetics) }
      { name: 'DEMO_DAILY_TIMER', value: dailyTimer }
      { name: 'DEMO_WATCHDOG_TIMER', value: watchdogTimer }
      { name: 'AZURE_DIAGNOSTIC_STORAGE_ACCOUNT', value: forwarding ? diagnosticName : '' }
      { name: 'AZURE_SUBSCRIPTION_ID', value: subscription().subscriptionId }
      { name: 'AZURE_RESOURCE_GROUP', value: resourceGroup().name }
      { name: 'AZURE_WORKER_RESOURCE_ID', value: resourceId('Microsoft.Web/sites', '${prefix}-worker-${take(suffix, 7)}') }
    ]) }
  }
  dependsOn: [packages, vaultRole, cosmosRole, storageRoles]
}
resource diagnosticStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = if (forwarding) { name: diagnosticName, location: location, tags: tags, sku: { name: 'Standard_LRS' }, kind: 'StorageV2', properties: { minimumTlsVersion: 'TLS1_2', allowBlobPublicAccess: false, supportsHttpsTrafficOnly: true, networkAcls: { defaultAction: 'Allow', bypass: 'AzureServices' } } }
resource diagnosticsLifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = if (forwarding) { parent: diagnosticStorage, name: 'default', properties: { policy: { rules: [{ name: 'expire-provider-logs', enabled: true, type: 'Lifecycle', definition: { filters: { blobTypes: ['blockBlob'] }, actions: { baseBlob: { delete: { daysAfterModificationGreaterThan: 7 } } } } }] } } }
resource diagnosticRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (forwarding) { name: guid(diagnosticStorage.id, identity.id), scope: diagnosticStorage, properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe') } }
resource monitorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (forwarding) { name: guid(resourceGroup().id, identity.id, 'monitor'), properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '43d0d8ad-25c7-4714-9337-8ba259a9fe05') } }
resource cosmosDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (forwarding) { scope: cosmos, name: '${prefix}-corridora', properties: { storageAccountId: diagnosticStorage.id, logs: [{ categoryGroup: 'allLogs', enabled: true }], metrics: [{ category: 'AllMetrics', enabled: true }] }, dependsOn: [newCosmos] }
resource vaultDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (forwarding) { scope: vault, name: '${prefix}-corridora', properties: { storageAccountId: diagnosticStorage.id, logs: [{ categoryGroup: 'allLogs', enabled: true }] } }
resource environmentDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (forwarding) { scope: environment, name: '${prefix}-corridora', properties: { storageAccountId: diagnosticStorage.id, logs: [{ categoryGroup: 'allLogs', enabled: true }] } }
output publicUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
output workerName string = worker.name
output keyVaultName string = vault.name
output storageAccount string = storage.name
output cosmosAccount string = cosmosName
output cosmosDatabase string = cosmosDatabase
output apiName string = api.name
output ownershipTag string = owner
output ownedResourceIds array = concat([identity.id, storage.id, vault.id, environment.id, api.id, flexPlan.id, worker.id, database.id, cosmosRole.id, vaultRole.id, extensionResourceId(storage.id, 'Microsoft.Authorization/roleAssignments', guid(storage.id, identity.id, 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')), extensionResourceId(storage.id, 'Microsoft.Authorization/roleAssignments', guid(storage.id, identity.id, '974c5e8b-45b9-4653-ba55-5f855dd0fb88'))], empty(existingCosmosName) ? [newCosmos!.id] : [], forwarding ? [cosmosDiagnostics!.id, vaultDiagnostics!.id, environmentDiagnostics!.id, diagnosticStorage!.id, diagnosticRole!.id, monitorRole!.id] : [])
