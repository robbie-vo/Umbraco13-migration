angular.module('umbraco.resources').factory('ncResource',
    function ($q, $http, umbRequestHelper) {
        return {
            getAll: function () {
                return umbRequestHelper.resourcePromise(
                    $http.get("api/migration/audit"),
                    "Failed to retrieve audit data");
            },

            update: function (data) {
                return umbRequestHelper.resourcePromise(
                    $http.post("api/migration/update", data),
                    "Failed to update property");
            }
        };
    }
);