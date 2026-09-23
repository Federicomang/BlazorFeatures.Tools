import "./lib/signalr/dist/browser/signalr.min.js";

const stateKey = Symbol.for("BlazorFeatures.Tools.EventBridge");

const state = globalThis[stateKey] ??= {
    localEndpoints: new Map(),
    signalREndpoints: new Map()
};

export function registerLocalEndpoint(endpointId, renderer, dotNetReference) {
    if (!endpointId) {
        throw new Error("An endpoint id is required.");
    }

    state.localEndpoints.set(endpointId, { renderer, dotNetReference });
}

export function unregisterLocalEndpoint(endpointId) {
    state.localEndpoints.delete(endpointId);
}

export async function publishLocalEvent(envelope) {
    const deliveries = [];
    for (const [endpointId, endpoint] of state.localEndpoints) {
        if (endpointId !== envelope.originEndpointId) {
            deliveries.push(
                endpoint.dotNetReference.invokeMethodAsync(
                    "ReceiveLocalEventAsync",
                    envelope));
        }
    }

    const results = await Promise.allSettled(deliveries);
    const rejected = results.filter(result => result.status === "rejected");
    if (rejected.length > 0) {
        throw new AggregateError(
            rejected.map(result => result.reason),
            "One or more local event receivers failed.");
    }

    return results.length;
}

export async function registerSignalRChannel(
    endpointId,
    channelName,
    hubUrl,
    dotNetReference) {
    if (!globalThis.signalR?.HubConnectionBuilder) {
        throw new Error(
            "The ASP.NET Core SignalR JavaScript client must be loaded before a SignalR event channel is used.");
    }
    if (state.signalREndpoints.has(endpointId)) {
        return;
    }

    const connection = new globalThis.signalR.HubConnectionBuilder()
        .withUrl(hubUrl, {
            accessTokenFactory: async () =>
                await dotNetReference.invokeMethodAsync(
                    "GetSignalRAccessTokenAsync") ?? ""
        })
        .withAutomaticReconnect()
        .build();

    const endpoint = {
        channelName,
        connection,
        dotNetReference,
        scopes: new Map(),
        disposed: false
    };
    connection.on("BlazorFeaturesReceiveEvent", envelope =>
        dotNetReference
            .invokeMethodAsync("ReceiveSignalREventAsync", envelope)
            .catch(error => console.error("A SignalR event receiver failed.", error)));
    connection.onreconnected(() => rejoinScopes(endpoint));
    connection.onclose(() => restartClosedConnection(endpoint));

    await startConnection(connection);
    state.signalREndpoints.set(endpointId, endpoint);
}

export async function unregisterSignalRChannel(endpointId) {
    const endpoint = state.signalREndpoints.get(endpointId);
    state.signalREndpoints.delete(endpointId);
    if (endpoint) {
        endpoint.disposed = true;
        await endpoint.connection.stop();
    }
}

export async function joinSignalRScope(endpointId, scope) {
    const endpoint = getSignalREndpoint(endpointId);
    const key = scopeKey(scope);
    if (endpoint.scopes.has(key)) {
        return;
    }

    await endpoint.connection.invoke("Join", scope);
    endpoint.scopes.set(key, scope);
}

export async function leaveSignalRScope(endpointId, scope) {
    const endpoint = getSignalREndpoint(endpointId);
    const key = scopeKey(scope);
    if (!endpoint.scopes.delete(key)) {
        return;
    }

    if (endpoint.connection.state === globalThis.signalR.HubConnectionState.Connected) {
        await endpoint.connection.invoke("Leave", scope);
    }
}

export async function publishSignalREvent(endpointId, request) {
    const endpoint = getSignalREndpoint(endpointId);
    return await endpoint.connection.invoke("Publish", request);
}

function getSignalREndpoint(endpointId) {
    const endpoint = state.signalREndpoints.get(endpointId);
    if (!endpoint) {
        throw new Error(`SignalR event endpoint '${endpointId}' is not registered.`);
    }
    return endpoint;
}

function scopeKey(scope) {
    return `${scope.kind}\u001f${scope.value}`;
}

async function rejoinScopes(endpoint) {
    for (const scope of endpoint.scopes.values()) {
        await endpoint.connection.invoke("Join", scope);
    }
}

async function restartClosedConnection(endpoint) {
    while (!endpoint.disposed) {
        try {
            await startConnection(endpoint.connection);
            await rejoinScopes(endpoint);
            return;
        } catch (error) {
            console.error("Unable to restart a SignalR event connection.", error);
            await new Promise(resolve => globalThis.setTimeout(resolve, 30000));
        }
    }
}

async function startConnection(connection) {
    const retryDelays = [0, 2000, 5000, 10000, 30000];
    let lastError;

    for (const delay of retryDelays) {
        if (delay > 0) {
            await new Promise(resolve => globalThis.setTimeout(resolve, delay));
        }

        try {
            await connection.start();
            return;
        } catch (error) {
            lastError = error;
        }
    }

    throw lastError ?? new Error("Unable to start the SignalR connection.");
}
