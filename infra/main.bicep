// AI Bug Investigation Agent — central Azure hosting (pilot).
// Container Apps (API + worker) · Service Bus queue · PostgreSQL Flexible Server (Entra auth only) ·
// Key Vault (secrets + Data Protection key) · Blob Storage (evidence, key ring) · ACR · Log Analytics / App Insights.
// Every service authenticates with ONE user-assigned managed identity; no connection-string secrets.
// Deploy (after explicit approval):  az deployment group create -g <rg> -f infra/main.bicep -p infra/main.parameters.json

targetScope = 'resourceGroup'

@description('Short name prefix, e.g. "bia". Lowercase letters/digits only.')
@minLength(3)
@maxLength(10)
param prefix string = 'bia'
param location string = resourceGroup().location

@description('Container image tags pushed to the registry created here.')
param apiImage string = 'bia-api:latest'
param workerImage string = 'bia-worker:latest'

@description('Forge app id (UUID) from `forge register`.')
param forgeAppId string
@description('Jira Cloud id(s) allowed to call the agent (your site only).')
param allowedJiraCloudIds array = []

@description('Application environment base URLs (central registry). Credentials are NEVER configured here.')
param kmsrcBaseUrl string = ''
param kmsqaBaseUrl string = ''

@description('Azure DevOps repository mapping for the pilot project.')
param adoOrganization string = ''
param adoProject string = ''
param adoRepository string = ''
param adoBranch string = 'main'
param jiraProjectKey string = ''

@description('Entra object id + name of the PostgreSQL Entra administrator group (DBAs).')
param postgresAdminGroupObjectId string
param postgresAdminGroupName string

param workerMaxReplicas int = 3

var suffix = uniqueString(resourceGroup().id)
var tags = { application: 'bug-investigation-agent' }

// ── Identity ─────────────────────────────────────────────────────────────────
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-id-${suffix}'
  location: location
  tags: tags
}

// ── Monitoring ───────────────────────────────────────────────────────────────
resource logs 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: '${prefix}-logs-${suffix}'
  location: location
  tags: tags
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: 30 }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${prefix}-ai-${suffix}'
  location: location
  kind: 'web'
  tags: tags
  properties: { Application_Type: 'web', WorkspaceResourceId: logs.id, DisableLocalAuth: false }
}

// ── Registry ─────────────────────────────────────────────────────────────────
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: '${prefix}acr${suffix}'
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false }
}

// ── Key Vault (RBAC) + Data Protection key ───────────────────────────────────
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${prefix}-kv-${suffix}'
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
  }
}

resource dataProtectionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: vault
  name: 'dataprotection'
  properties: { kty: 'RSA', keySize: 2048, keyOps: [ 'wrapKey', 'unwrapKey' ] }
}

// ── Storage (evidence + Data Protection key ring), Entra-only access ─────────
resource storage 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: '${prefix}st${suffix}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: storage
  name: 'default'
}

resource evidenceContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: 'evidence'
  properties: { publicAccess: 'None' }
}

resource dpContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: 'dataprotection'
  properties: { publicAccess: 'None' }
}

// ── Service Bus queue ────────────────────────────────────────────────────────
resource bus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: '${prefix}-sb-${suffix}'
  location: location
  tags: tags
  sku: { name: 'Standard', tier: 'Standard' }
  properties: { disableLocalAuth: true, minimumTlsVersion: '1.2' }
}

resource queue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: bus
  name: 'investigations'
  properties: { lockDuration: 'PT1M', maxDeliveryCount: 10, deadLetteringOnMessageExpiration: true }
}

// ── PostgreSQL Flexible Server (Entra ID auth only — no passwords) ───────────
resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2023-06-01-preview' = {
  name: '${prefix}-pg-${suffix}'
  location: location
  tags: tags
  sku: { name: 'Standard_B1ms', tier: 'Burstable' }
  properties: {
    version: '16'
    storage: { storageSizeGB: 32 }
    authConfig: { activeDirectoryAuth: 'Enabled', passwordAuth: 'Disabled', tenantId: subscription().tenantId }
    backup: { backupRetentionDays: 7, geoRedundantBackup: 'Disabled' }
    highAvailability: { mode: 'Disabled' }
  }
}

resource postgresAdmin 'Microsoft.DBforPostgreSQL/flexibleServers/administrators@2023-06-01-preview' = {
  parent: postgres
  name: postgresAdminGroupObjectId
  properties: { principalType: 'Group', principalName: postgresAdminGroupName, tenantId: subscription().tenantId }
}

resource postgresDb 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2023-06-01-preview' = {
  parent: postgres
  name: 'bugagent'
}

resource postgresAzureAccess 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2023-06-01-preview' = {
  parent: postgres
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

// ── RBAC for the managed identity (least privilege) ──────────────────────────
var roles = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  kvSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  kvCryptoUser: '12338af0-0e69-4776-bea7-57ae8d297424'
  blobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  busSender: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  busReceiver: '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: acr
  name: guid(acr.id, identity.id, roles.acrPull)
  properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull) }
}

resource kvSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, identity.id, roles.kvSecretsUser)
  properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.kvSecretsUser) }
}

resource kvCrypto 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: dataProtectionKey
  name: guid(dataProtectionKey.id, identity.id, roles.kvCryptoUser)
  properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.kvCryptoUser) }
}

resource blobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, identity.id, roles.blobDataContributor)
  properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.blobDataContributor) }
}

resource busSend 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: queue
  name: guid(queue.id, identity.id, roles.busSender)
  properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.busSender) }
}

resource busReceive 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: queue
  name: guid(queue.id, identity.id, roles.busReceiver)
  properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.busReceiver) }
}

// ── Container Apps ───────────────────────────────────────────────────────────
resource managedEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${prefix}-cae-${suffix}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: { customerId: logs.properties.customerId, sharedKey: logs.listKeys().primarySharedKey }
    }
  }
}

var commonEnv = [
  { name: 'Hosting__ManagedIdentityClientId', value: identity.properties.clientId }
  { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
  { name: 'Hosting__DatabaseProvider', value: 'PostgreSQL' }
  { name: 'Hosting__UseManagedIdentityForDatabase', value: 'true' }
  { name: 'Hosting__DatabaseConnectionString', value: 'Host=${postgres.properties.fullyQualifiedDomainName};Database=bugagent;Username=${identity.name};SSL Mode=Require' }
  { name: 'Hosting__KeyVaultUri', value: vault.properties.vaultUri }
  { name: 'Hosting__ServiceBusNamespace', value: '${bus.name}.servicebus.windows.net' }
  { name: 'Hosting__BlobServiceUri', value: storage.properties.primaryEndpoints.blob }
  { name: 'Hosting__DataProtectionKeyId', value: dataProtectionKey.properties.keyUri }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
  { name: 'Forge__AppId', value: forgeAppId }
  { name: 'EnvironmentRegistry__Environments__KMSRC__BaseUrl', value: kmsrcBaseUrl }
  { name: 'EnvironmentRegistry__Environments__KMSQA__BaseUrl', value: kmsqaBaseUrl }
]
var cloudIdEnv = [for (id, i) in allowedJiraCloudIds: { name: 'Forge__AllowedCloudIds__${i}', value: id }]

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-api'
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  properties: {
    managedEnvironmentId: managedEnv.id
    configuration: {
      ingress: { external: true, targetPort: 8080, transport: 'http', allowInsecure: false }
      registries: [ { server: acr.properties.loginServer, identity: identity.id } ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: '${acr.properties.loginServer}/${apiImage}'
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: concat(commonEnv, cloudIdEnv, [ { name: 'Forge__PublicBaseUrl', value: 'https://${prefix}-api.${managedEnv.properties.defaultDomain}' } ])
          probes: [
            { type: 'Liveness', httpGet: { path: '/healthz', port: 8080 } }
            { type: 'Readiness', httpGet: { path: '/readyz', port: 8080 } }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
  dependsOn: [ acrPull, kvSecrets, kvCrypto, blobRole, busSend ]
}

resource worker 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-worker'
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  properties: {
    managedEnvironmentId: managedEnv.id
    configuration: {
      // No ingress: the worker is not reachable from outside.
      registries: [ { server: acr.properties.loginServer, identity: identity.id } ]
    }
    template: {
      containers: [
        {
          name: 'worker'
          image: '${acr.properties.loginServer}/${workerImage}'
          resources: { cpu: json('2.0'), memory: '4Gi' }
          env: concat(commonEnv, [
            { name: 'AzureDevOps__ManagedIdentityClientId', value: identity.properties.clientId }
            { name: 'Repositories__Mappings__0__JiraProject', value: jiraProjectKey }
            { name: 'Repositories__Mappings__0__Organization', value: adoOrganization }
            { name: 'Repositories__Mappings__0__Project', value: adoProject }
            { name: 'Repositories__Mappings__0__Repository', value: adoRepository }
            { name: 'Repositories__Mappings__0__Branch', value: adoBranch }
          ])
          probes: [
            { type: 'Liveness', httpGet: { path: '/healthz', port: 8080 } }
          ]
        }
      ]
      // Keep one worker warm (sessions awaiting input live in a worker); scale on queue depth.
      scale: {
        minReplicas: 1
        maxReplicas: workerMaxReplicas
        rules: [
          {
            name: 'queue-depth'
            custom: {
              type: 'azure-servicebus'
              metadata: { queueName: 'investigations', namespace: bus.name, messageCount: '2' }
              identity: identity.id
            }
          }
        ]
      }
    }
  }
  dependsOn: [ acrPull, kvSecrets, kvCrypto, blobRole, busReceive ]
}

output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
output managedIdentityClientId string = identity.properties.clientId
output managedIdentityPrincipalId string = identity.properties.principalId
output managedIdentityName string = identity.name
output keyVaultName string = vault.name
output registryLoginServer string = acr.properties.loginServer
output postgresServer string = postgres.properties.fullyQualifiedDomainName
