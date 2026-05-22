angular.module('umbraco.resources').factory('mpResource',
    function ($q, $http, umbRequestHelper) {
        return {
            getAll: function () {
                return umbRequestHelper.resourcePromise(
                    $http.get("api/mediapickermigration/audit"),
                    "Failed to retrieve media picker audit data");
            },

            fix: function (data) {
                return umbRequestHelper.resourcePromise(
                    $http.post("api/mediapickermigration/fix", data),
                    "Failed to fix media picker data type");
            }
        };
    }
);
