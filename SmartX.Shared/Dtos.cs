using System;
using System.Collections.Generic;
using System.Text;

namespace SmartX.Shared;

public record SensorRegistrationRequest(string MacAddress, string Location, string Category);
public record TelemetryIngestRequest(string MacAddress, double Value, string Unit);
public record PathValidationRequest(List<string> Path);

//---------------------------------------------------END-------------------------------//